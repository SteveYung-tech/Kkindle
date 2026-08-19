using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MdxParser.Models
{
    public enum EncryptionType
    {
        Lzo = 0x01,
        Zlib = 0x02,
        Hashed = 0x10,
        ZlibAndHashed = 0x12,
        Salsa = 0x20,
        ZlibAndSalsa = 0x22,
        None = 0x00,
    }
    public interface IBlocks
    {
        long StartIndex { get; }
        long EndIndex { get; }
        long Size { get; }
        byte[] getDecompressedData();
    }
}
