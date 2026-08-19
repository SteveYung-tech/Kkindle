using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using MdxParser.encrypt;
using System.Text;
using System.Text.Json.Serialization;

namespace MdxParser.Models
{
    public class KeyBlock : AbsoluteBlock, IBlocks
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
        public KeyBlock(MdxBlock mdxBlock, long start)
        {
            this.mdxBlock = mdxBlock;
            this.StartIndex = start;
            parseKeyBlock(mdxBlock.Stream);
        }
        private void parseKeyBlock(Stream stream)
        {
            if (stream.Position != StartIndex)
                stream.Position = StartIndex;

            DecompressedSize = readUInt32(stream);
            CompressedSize = readUInt32(stream);
            readBytes(stream, Encryption);
            readBytes(stream, Adler32Data);

            //m_EncryptedData = new byte[EncryptedSize];
            //readBytes(stream, m_EncryptedData);
            Data = new byte[CompressedSize-8];
            readBytes(stream, m_Data);

            Size = 8 + CompressedSize;
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


            //Stream stream = File.OpenWrite(AppDomain.CurrentDomain.BaseDirectory + size + ".lzo");
            //stream.Write(decryptedBlock);
            //stream.Close();

            // check adler checksum over decrypted data
            if (mdxBlock.Document.Version >= 3.0)
            {
                var computeAdler = adler32Compute(m_DecryptedData);
                var checksum = BitConverter.ToUInt32(Adler32Data).CompareTo(computeAdler);
            }
            //if self._version >= 3:
            //    assert(hex(adler32) == hex(zlib.adler32(decrypted_block) & 0xffffffff))

            byte[] m_DecompressData;
            // decompress
            if (EncryptedType == EncryptionType.None)
                m_DecompressData = m_DecryptedData;
            else if (EncryptedType == EncryptionType.ZlibAndHashed || EncryptedType == EncryptionType.ZlibAndSalsa)
            {
                m_DecompressData = ZipDecompress(m_DecryptedData);
            }
            else
                throw new Exception(string.Format("compression method {0} not supported", EncryptedType));
            return m_DecompressData;
            // 3.0版本起已废弃lzo压缩格式
            //else if (compression_method == 1)
            //{

            //    byte[] compressedBlock = new byte[5 + decryptedBlock.Length];
            //    compressedBlock[0] = (byte)0xf0;
            //    Array.Copy(BitConverter.GetBytes(size).Reverse().ToArray(), 0, compressedBlock, 1, 4);
            //    Array.Copy(decryptedBlock, 0, compressedBlock, 5, decryptedBlock.Length);
            //    decompressedBlock = LzoDecompress(decryptedBlock);

            //}
            //else { throw new Exception(string.Format("compression method {0} not supported", compression_method)); }



            //# check adler checksum over decompressed data
            //if self._version < 3:
            //    assert(hex(adler32) == hex(zlib.adler32(decompressed_block) & 0xffffffff))
        }
        private byte[] ZipDecompress(byte[] data)
        {
            MemoryStream compressed = new MemoryStream(data);
            MemoryStream decompressed = new MemoryStream();
            InflaterInputStream inputStream = new InflaterInputStream(compressed);
            inputStream.CopyTo(decompressed);
            return decompressed.ToArray();
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
