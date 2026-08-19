using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MdxParser.Models
{
    public class KeyData : AbsoluteBlock
    {
        public long Id { get; set; }
        public string Text { get; set; }

        public static KeyData of(long id, string text)
        {
            return new KeyData { Id = id, Text = text };
        }
    }
}
