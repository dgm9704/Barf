using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Xunit;

namespace Diwen.Aifmd.Tests
{
    public class OrderingTests
    {

        [Fact]
        public void OrderingTest()
        {
            var document = XDocument.Load("data/unordered.xml");
            var root = document.Root;

            var reader = root.CreateReader();

            XmlWriterSettings settings = new XmlWriterSettings
            {
                ConformanceLevel = ConformanceLevel.Fragment,
                Indent = false,
                OmitXmlDeclaration = false
            };

            StringBuilder sb = new StringBuilder();
            var xw = XmlWriter.Create(sb, settings);

            var xr = XmlReader.Create("data/transform.xslt");

            var xslt = new XslCompiledTransform();
            xslt.Load(xr);

            // Execute the transform
            xslt.Transform(reader, xw);

            // Swap the old control element for the new one
            root.ReplaceWith(XElement.Parse(sb.ToString()));

            document.Save("data/ordered.xml");

        }

    }
}