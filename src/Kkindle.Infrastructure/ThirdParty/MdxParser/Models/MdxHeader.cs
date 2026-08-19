using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace MdxParser.Models
{
    [XmlRoot("ZDB")]
    public class MdxHeader : AbsoluteBlock
    {
        [XmlAttribute]
        public string? UUID { get; set; }
        [XmlAttribute]
        public float GeneratedByEngineVersion { get; set; }
        [XmlAttribute]
        public float RequiredEngineVersion { get; set; }

        [XmlAttribute]
        public string? Encrypted { get; set; }
        [XmlAttribute]
        public string? Encoding { get; set; }

        [XmlAttribute]
        public string? Format { get; set; }
        [XmlAttribute]
        public string? Compact { get; set; }
        [XmlAttribute]
        public string? Compat { get; set; }
        [XmlAttribute]
        public string? KeyCaseSensitive { get; set; }
        [XmlAttribute]
        public string? Description { get; set; }
        [XmlAttribute]
        public string? Title { get; set; }
        [XmlAttribute]
        public string? DataSourceFormat { get; set; }
        [XmlAttribute]
        public string? StyleSheet { get; set; }
    }
}
