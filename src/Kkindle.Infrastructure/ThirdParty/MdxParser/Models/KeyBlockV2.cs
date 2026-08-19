using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using MdxParser.encrypt;
using System.Text;
using System.Text.Json.Serialization;

namespace MdxParser.Models
{
    public class KeyBlockV2 : AbsoluteBlock, IBlocks
    {
        private uint m_DecompressedSize;
        private uint m_CompressedSize;
        private byte[] m_Encryption = new byte[4];
        private byte[] m_Adler32Data = new byte[4];
        private byte[] m_EncryptedKey;

        private byte[] m_Data;
        private byte[] m_EncryptedData;
        private byte[] m_DecryptedData;
        private MdxBlock mdxBlock;
        public KeyBlockV2(MdxBlock mdxBlock, long start, uint compressedSize, uint decompressedSize)
        {
            this.mdxBlock = mdxBlock;
            this.StartIndex = start;
            this.m_CompressedSize = compressedSize;
            this.m_DecompressedSize = decompressedSize;
            parseKeyBlock(mdxBlock.Stream);
        }
        private void parseKeyBlock(Stream stream)
        {
            if (stream.Position != StartIndex)
                stream.Position = StartIndex;

            readBytes(stream, Encryption);
            readBytes(stream, Adler32Data);

            //m_EncryptedData = new byte[EncryptedSize];
            //readBytes(stream, m_EncryptedData);
            Data = new byte[CompressedSize-8];
            readBytes(stream, m_Data);

            Size = CompressedSize;
            //var decompressedBlockData = decodeBlock(blockData, decompressedSize);
            //keyList.AddRange(splitKeyBlock(decompressedBlockData));
        }
        [JsonIgnore]
        public byte[] EncryptedKey
        {
            get
            {   if(mdxBlock.Document.EncryptKey != null)
                    m_EncryptedKey = mdxBlock.Document.EncryptKey;
                if (m_EncryptedKey == null)
                {
                    // adler checksum of the block data used as the encryption key if none given
                    m_EncryptedKey = ripemd128(Adler32Data);
                    return m_EncryptedKey;
                }
                return m_EncryptedKey;
            }
        }

        public byte[] getDecompressedData()
        {
            switch (EncryptedType)
            {
                case EncryptionType.None:
                default:
                    m_DecryptedData = m_Data;
                    break;
                case EncryptionType.Hashed:
                case EncryptionType.ZlibAndHashed:
                    var decryptBuff = fastDecrypt(Data[0..EncryptedSize], EncryptedKey);
                    m_DecryptedData = new byte[decryptBuff.Length + Data.Length - EncryptedSize];
                    Array.Copy(decryptBuff, m_DecryptedData, decryptBuff.Length);
                    Array.Copy(Data[EncryptedSize..], 0, m_DecryptedData, decryptBuff.Length, Data.Length - EncryptedSize);
                    break;
                case EncryptionType.Salsa:
                case EncryptionType.ZlibAndSalsa:
                    var salsaBuff = salsaDecrypt(Data[0..EncryptedSize], EncryptedKey);
                    m_DecryptedData = new byte[salsaBuff.Length + Data.Length - EncryptedSize];
                    Array.Copy(salsaBuff, m_DecryptedData, salsaBuff.Length);
                    Array.Copy(Data[EncryptedSize..], 0, m_DecryptedData, salsaBuff.Length, Data.Length - EncryptedSize);
                    break;
            }

            // check adler checksum over decrypted data
            if (mdxBlock.Document.Version >= 3.0)
            {
                var computeAdler = adler32Compute(m_DecryptedData);
                var checksum = BitConverter.ToUInt32(Adler32Data).CompareTo(computeAdler);
            }

            byte[] m_DecompressData;
            // decompress
            switch (EncryptedType)
            {
                case EncryptionType.Lzo:
                    //C#版解压缩无需增加lzo的header
                    //byte[] compressedBlock = new byte[5 + m_DecryptedData.Length];
                    //compressedBlock[0] = (byte)0xf0;
                    //Array.Copy(BitConverter.GetBytes(m_CompressedSize).Reverse().ToArray(), 0, compressedBlock, 1, 4);
                    //Array.Copy(m_DecryptedData, 0, compressedBlock, 5, m_DecryptedData.Length);
                    m_DecompressData = LzoDecompress(m_DecryptedData, m_DecompressedSize);
                    break;
                case EncryptionType.Zlib:
                case EncryptionType.ZlibAndSalsa:
                case EncryptionType.ZlibAndHashed:
                    m_DecompressData = ZipDecompress(m_DecryptedData);
                    break;
                case EncryptionType.None:
                    m_DecompressData = m_DecryptedData;
                    break;
                default:
                    throw new Exception(string.Format("compression method {0} not supported", EncryptedType));
            }
            return m_DecompressData;
            // 3.0版本起已废弃lzo压缩格式
        }
        public int EncryptedSize
        {
            get => Encryption[1];
        }
        /// <summary>
        /// Encryption Compression Info
        /// </summary>
        public EncryptionType EncryptedType
        {
            get
            {
                switch (Encryption[0])
                {
                    case 0x01:
                        return EncryptionType.Lzo;
                    case 0x02:
                        return EncryptionType.Zlib;
                    case 0x10:
                        return EncryptionType.Hashed;
                    case 0x12:
                        return EncryptionType.ZlibAndHashed;
                    case 0x20:
                        return EncryptionType.Salsa;
                    case 0x22:
                        return EncryptionType.ZlibAndSalsa;
                    default:
                        return EncryptionType.None;
                }
            }
        }

        public uint DecompressedSize { get => m_DecompressedSize; set => m_DecompressedSize = value; }
        public uint CompressedSize { get => m_CompressedSize; set => m_CompressedSize = value; }
        [JsonIgnore]
        public byte[] Encryption { get => m_Encryption; set => m_Encryption = value; }
        [JsonIgnore]
        public byte[] Adler32Data { get => m_Adler32Data; set => m_Adler32Data = value; }
        [JsonIgnore]
        public byte[] Data { get => m_Data; set => m_Data = value; }
    }
}
