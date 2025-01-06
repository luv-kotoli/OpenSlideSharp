using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using BruTile;
using BruTile.Cache;

namespace OpenSlideSharp.BruTile.DPTSlide
{
    public class DPTSlideBase : SlideSourceBase
    {
        public DPTWSIFile DptFile;
        private Stream Stream;
        private BinaryReader Reader;
        private ConcurrentDictionary<ImagePosInfo, ImageDataInfo> ImageInfos = new ConcurrentDictionary<ImagePosInfo, ImageDataInfo>();
        public int WSIWidth;
        public int WSIHeight;
        private float[,] CCM;

        // 添加缓存部分
        private readonly bool _enableCache;
        private readonly MemoryCache<byte[]> _tileCache = new MemoryCache<byte[]>();

        public DPTSlideBase(string filePath, bool enableCache = true)
        {
            
            DptFile = new DPTWSIFile(filePath);
            Stream = DptFile.GetFileStream();
            Reader = DptFile.GetReader();
            CCM = new float[,] 
            {
                {1.5359419584274292f, -0.4762600064277649f, -0.06893599778413773f },
                {-0.453308999f, 1.7416930198f, -0.31106099486351013f },
                {-0.050136998295784f, -0.5789409875869751f, 1.590363979f }
            };

            int imageNum = (int)DptFile.ImageNum;

            // 初始化ImageInfos对象
            // 将ImageInfo部分读入内存
            DptFile.GetFileStream().Seek(DPTWSIFileConsts.ImageInfoStartOffset, SeekOrigin.Begin);
            byte[] imageInfoBytes = Reader.ReadBytes(imageNum * DPTWSIFileConsts.ImageInfoSize);
            ConcurrentBag<int> xPosList = new ConcurrentBag<int>(); // 用来记录实际的全场图宽度
            ConcurrentBag<int> yPosList = new ConcurrentBag<int>(); // 用来记录实际的全场图宽度

            Parallel.For(0, imageNum, i =>
            {
                int startOffset = i * DPTWSIFileConsts.ImageInfoSize;
                ImagePosInfo posInfo = new ImagePosInfo()
                {
                    Layer = (sbyte)imageInfoBytes[startOffset],
                    X = BitConverter.ToUInt32(imageInfoBytes, startOffset + 1),
                    Y = BitConverter.ToUInt32(imageInfoBytes, startOffset + 5),
                    Z = imageInfoBytes[startOffset + 9],
                };
                xPosList.Add(BitConverter.ToInt32(imageInfoBytes, startOffset + 1));
                yPosList.Add(BitConverter.ToInt32(imageInfoBytes, startOffset + 5));
                ImageDataInfo dataInfo = new ImageDataInfo()
                {
                    Length = BitConverter.ToInt32(imageInfoBytes, startOffset + 10),
                    Offset = BitConverter.ToInt64(imageInfoBytes, startOffset + 14),
                };
                ImageInfos.TryAdd(posInfo, dataInfo);
            });

            WSIWidth = xPosList.Max() + DptFile.SingleImageWidth;
            WSIHeight = yPosList.Max() + DptFile.SingleImageHeight;

            // 对父类的属性进行实现
            Source = filePath;
            MinUnitsPerPixel = UseRealResolution ? DptFile.Mpp : 1;
            if (MinUnitsPerPixel <= 0) MinUnitsPerPixel = 1;

            var width = WSIWidth * MinUnitsPerPixel;
            var height = WSIHeight * MinUnitsPerPixel;

            ExternInfo = new Dictionary<string, object>();
            Schema = new TileSchema
            {
                YAxis = YAxis.OSM,
                Format = "jpg",
                Extent = new Extent(0,-height,width,0),
                OriginX = 0,
                OriginY = 0,
            };
            InitResolutions(Schema.Resolutions, 512, 512);

            // 缓存处理部分
            _enableCache = true;
        }

        /// <summary>
        /// 用于读取图像中一定区域内的数据
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="width">实际要取得的图像宽度</param>
        /// <param name="height">实际要取得的图像高度</param>
        /// <param name="realHeight"></param>
        /// <param name="realWidth"></param>
        /// <param name="layer"></param>
        /// <param name="z"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public Mat ReadRegion(int x, int y, int width, int height, int layer, out int realWidth, out int realHeight, int z = 0)
        {
            if (layer < 0 || layer > 2)
            {
                throw new ArgumentException("Layer 只能为0,1,2层");
            }

            double scale = 1 / Math.Pow(4, layer);

            // 按照缩放比例将x,y 转换为当前层的坐标
            int scaledX = (int)(x * scale);
            int scaledY = (int)(y * scale);

            // 缩放后的全场图宽度
            int scaledWSIWidth = (int)(DptFile.Width * scale);
            int scaledWSIHeight = (int)(DptFile.Height * scale);

            // 计算图像外围内的实际终点
            int realRegionStartX = Math.Max(0, scaledX);
            int realRegionStartY = Math.Max(0, scaledY);
            int realRegionEndX = Math.Min(scaledWSIWidth - 1, scaledX + width - 1);
            int realRegionEndY = Math.Min(scaledWSIHeight - 1, scaledY + height - 1);

            // 当前尺度下单张图的实际大小
            int originTileSizeX = (int)(DptFile.SingleImageWidth - DptFile.Overlap);
            int originTileSizeY = (int)(DptFile.SingleImageHeight - DptFile.Overlap);
            int tileSizeX = (int)(originTileSizeX * scale);
            int tileSizeY = (int)(originTileSizeY * scale);

            ConcurrentBag<ImagePosInfo> posInfos = new ConcurrentBag<ImagePosInfo>();
            // 计算起终点所处的小图序号
            int left = realRegionStartX / tileSizeX;
            int right = realRegionEndX / tileSizeX;
            int top = realRegionStartY / tileSizeY;
            int bottom = realRegionEndY / tileSizeY;

            // 创建多线程读取PosInfoList
            Parallel.For(left, right + 1, imageX =>
            {
                for (int imageY = top; imageY < bottom + 1; imageY++)
                {
                    // tileX,Y 用来表示读取的tile的像素位置
                    uint tileX = (uint)(imageX * tileSizeX / scale);
                    uint tileY = (uint)(imageY * tileSizeY / scale);
                    if (tileX + DptFile.SingleImageWidth > WSIWidth || tileY + DptFile.SingleImageHeight > WSIHeight) continue;
                    var posInfo = new ImagePosInfo()
                    {
                        Layer = (sbyte)layer,
                        X = tileX,
                        Y = tileY,
                        Z = (byte)z,
                    };
                    posInfos.Add(posInfo);
                }
            });


            ConcurrentDictionary<ImagePosInfo, Mat> tiles = new ConcurrentDictionary<ImagePosInfo, Mat>();

            Parallel.ForEach(posInfos, (posInfo) =>
            {
                try
                {
                    ImageInfos.TryGetValue(posInfo, out var dataInfo);
                    Stream readStream = new FileStream(DptFile.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Mat tile = ReadSingleImageData(posInfo, readStream);
                    tiles[posInfo] = tile;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    tiles[posInfo] = new Mat();
                }
            });

            // 将区域融合为一个Mat
            Mat regionMat = new Mat(new Size(realRegionEndX - scaledX + 1, realRegionEndY - scaledY + 1), MatType.CV_8UC3, new Scalar(0, 0, 0));
            using Mat failedImgMat = new Mat(new Size((int)(DptFile.SingleImageWidth * scale), (int)(DptFile.SingleImageHeight * scale)),
                                           MatType.CV_8UC3, new Scalar(251, 251, 251));
            int idx = 0; //序号：用于保存
            foreach (var jpegImg in tiles)
            {
                // 读取jpeg数据
                List<FusionDirection> directions = CalculateTileFusionDirection(jpegImg.Key);
                using Mat singleImgMat = jpegImg.Value.Size() == new Size(0, 0) ? failedImgMat : jpegImg.Value.Resize(failedImgMat.Size());
                Cv2.Resize(singleImgMat, singleImgMat, new Size(DptFile.SingleImageWidth * scale, DptFile.SingleImageHeight * scale));
                Cv2.CvtColor(singleImgMat, singleImgMat, ColorConversionCodes.RGB2BGR);

                //if (layer == 0)
                //{
                DPTSlideUtils.WeightedFusionSingle(singleImgMat, (int)(DptFile.Overlap / Math.Pow(4, jpegImg.Key.Layer)), directions, kValue: 0.15 * Math.Pow(4, jpegImg.Key.Layer));
                //}

                // 测试: 保存所有tile
                //singleImgMat.SaveImage($"D:/yuxx/dpt_write_test/{jpegImg.Key.X}-{jpegImg.Key.Y}.jpg");

                int imgX = (int)(jpegImg.Key.X * scale);
                int imgY = (int)(jpegImg.Key.Y * scale);
                int imgWidth = (int)(DptFile.SingleImageWidth * scale);
                int imgHeight = (int)(DptFile.SingleImageHeight * scale);

                //singleImgMat.SaveImage("D:/yuxx/test_dpt_read.jpg");

                // 处理截取 scaledX，Y: 所取region起始位置, imgX,Y: 当前patch 左上角位置, realRegionEndX,Y: 所取region终点位置
                int startX = Math.Max(scaledX, imgX);
                int startY = Math.Max(scaledY, imgY);
                int endX = Math.Min(realRegionEndX, imgX + imgWidth - 1);
                int endY = Math.Min(realRegionEndY, imgY + imgHeight - 1);
                //Console.WriteLine($"regionMat Size:{regionMat.Size()}, End Position:{endX - scaledX}-{endY - scaledY}");
                using Mat roi = singleImgMat.SubMat(startY - imgY, endY - imgY, startX - imgX, endX - imgX);
                using Mat subRegion = regionMat.SubMat(startY - scaledY, endY - scaledY, startX - scaledX, endX - scaledX);
                //roi.CopyTo(subRegion);

                // 用于融合拼缝
                /*if (layer == 0) */
                Cv2.Add(roi, subRegion, subRegion);
                //else roi.CopyTo(subRegion);

                //regionMat.SaveImage("D:/yuxx/test_dpt_read2.jpg");
                idx++;
            }

            // TODO: 过大数组会导致内存溢出,需要优化
            //regionMat.SaveImage("D:/yuxx/test_dpt_read.jpg");
            //byte[] result = new byte[regionMat.Width * regionMat.Height * 3];
            //Marshal.Copy(regionMat.Data, result, 0, result.Length);
            realWidth = regionMat.Width;
            realHeight = regionMat.Height;
            return regionMat;
        }


        public override byte[] GetTile(TileInfo info)
        {
            if (info == null) return null;
            if (_enableCache && _tileCache.Find(info.Index) is byte[] output) return output;

            // 获取取得tile的大小
            var curLevel = info.Index.Level;
            var tileWidth = Schema.Resolutions[curLevel].TileWidth;
            var tileHeight = Schema.Resolutions[curLevel].TileHeight;
            //var curlevelOffsetXPixel = info.Index.Col * tileWidth * Math.Pow(4, curLevel);
            //var curlevelOffsetYPixel = info.Index.Row * tileHeight * Math.Pow(4,curLevel);

            var rgbData = GetTile(info.Index.Row, info.Index.Col, 0, tileWidth, curLevel);

            // 写入缓存
            if (_enableCache && rgbData != null)
                _tileCache.Add(info.Index, rgbData);

            return rgbData;

        }

        public byte[] GetTile(int row, int col, int z, int tileSize, int layer)
        {
            Mat realTileMat = new Mat(new Size(tileSize, tileSize), MatType.CV_8UC3, new Scalar(251,251,251));
            int btmLayerPosX = col * tileSize * (int)Math.Pow(4, layer);
            int btmLayerPosY = row * tileSize * (int)Math.Pow(4, layer);

            Mat tileMat = ReadRegion(btmLayerPosX, btmLayerPosY, tileSize, tileSize, layer, out int  realWidth, out int realHeight, z: z);

            // 颜色校正
            tileMat = ApplyCCM(tileMat, CCM);

            // 针对边界tile的处理，当tile实际大小与tileSize不同时
            // 确保tile大小为tileSize*tileSize
            if (realWidth != tileSize || realHeight != tileSize)
            {
                tileMat.CopyTo(realTileMat[new Rect(0, 0, realWidth, realHeight)]);
            }
            else tileMat.CopyTo(realTileMat);

            // jpeg 压缩
            var jpegData = realTileMat.ImEncode(".jpg");
            tileMat.Dispose();
            realTileMat.Dispose();
            return jpegData;
        }

        
        public override IReadOnlyDictionary<string, byte[]> GetExternImages() { return new Dictionary<string, byte[]>(); }
        public int GetBestDownsampleLayer()
        {
            return 0;
        }

        /// <summary>
        /// 读取单张图像的jpeg二进制数据
        /// </summary>
        /// <param name="posInfo"></param>
        /// <returns></returns>
        public Mat ReadSingleImageData(ImagePosInfo posInfo, Stream readStream)
        {
            using (readStream)
            using (BinaryReader reader = new BinaryReader(readStream))
            {
                ImageDataInfo dataInfo = ImageInfos[posInfo];
                readStream.Seek(dataInfo.Offset, SeekOrigin.Begin);
                byte[] imageData = reader.ReadBytes(dataInfo.Length);
                if (imageData[0] != 0XFF || imageData[1] != 0XD8)
                    throw new Exception($"The Magic Number is {imageData[0]} {imageData[1]}, Not jpeg data ");
                if (imageData == null)
                    throw new Exception($"This Offset contains no data");
                Mat jpegMat = Cv2.ImDecode(imageData, ImreadModes.Color);
                return jpegMat;
            }
        }

        public List<FusionDirection> CalculateTileFusionDirection(ImagePosInfo info)
        {
            List<FusionDirection> fusionDirections = new List<FusionDirection>();
            if (info.X != 0) fusionDirections.Add(FusionDirection.Left);
            if (info.Y != 0) fusionDirections.Add(FusionDirection.Top);
            if (info.X + DptFile.SingleImageWidth != WSIWidth) fusionDirections.Add(FusionDirection.Right);
            if (info.Y + DptFile.SingleImageHeight != WSIHeight) fusionDirections.Add(FusionDirection.Bottom);

            return fusionDirections;
        }

        protected void InitResolutions(IDictionary<int, Resolution> resolutions, int tileWidth, int tileHeight)
        {
            for (int i = 0; i<3; i++)
            {
                resolutions.Add(i, new Resolution(i, MinUnitsPerPixel * Math.Pow(4,i),tileWidth, tileHeight));
            }
        }

        private static Mat ApplyCCM(Mat inputImage, float[,] ccm)
        {
            if (inputImage == null || inputImage.Empty())
                throw new ArgumentException("Input image is null or empty.");
            if (inputImage.Type() != MatType.CV_8UC3)
                throw new ArgumentException("Input image must be of type CV_8UC3.");
            if (ccm.GetLength(0) != 3 || ccm.GetLength(1) != 3)
                throw new ArgumentException("CCM must be a 3x3 matrix.");

            // Create an output image with the same size and type
            Mat outputImage = new Mat(inputImage.Size(), inputImage.Type());

            // Iterate through each pixel
            for (int row = 0; row < inputImage.Rows; row++)
            {
                for (int col = 0; col < inputImage.Cols; col++)
                {
                    // Get the current pixel's BGR values as Vec3b (byte per channel)
                    Vec3b pixel = inputImage.At<Vec3b>(row, col);

                    // Apply the CCM to the pixel
                    float newBlue = pixel[0] * ccm[0, 0] + pixel[1] * ccm[0, 1] + pixel[2] * ccm[0, 2];
                    float newGreen = pixel[0] * ccm[1, 0] + pixel[1] * ccm[1, 1] + pixel[2] * ccm[1, 2];
                    float newRed = pixel[0] * ccm[2, 0] + pixel[1] * ccm[2, 1] + pixel[2] * ccm[2, 2];

                    // Clamp the values to [0, 255] and assign to the new pixel
                    Vec3b correctedPixel = new Vec3b
                    {
                        Item0 = (byte)(newBlue <0 ? 0 : (newBlue>255 ? 255: newBlue)),
                        Item1 = (byte)(newGreen < 0 ? 0 : (newGreen > 255 ? 255 : newGreen)),
                        Item2 = (byte)(newRed < 0 ? 0 : (newRed > 255 ? 255 : newRed))
                    };

                    // Set the corrected pixel to the output image
                    outputImage.Set(row, col, correctedPixel);
                }
            }
            return outputImage;
        }
    }
}
