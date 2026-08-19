using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MdxParser.Models
{
    public class RecordData : AbsoluteBlock
    {
        public byte[] Data { get; private set; }
        public string Key { get; set; }
        public string Text { get; private set; }

        public bool IsBinary {  get; private set; }

        public static RecordData of(string key, byte[] data)
        {
            return new RecordData { Key = key, Data = data, IsBinary = true };
        }
        public static RecordData of(string key, string text)
        {
            return new RecordData { Key = key, Text = text, IsBinary = false };
        }
    }
}
