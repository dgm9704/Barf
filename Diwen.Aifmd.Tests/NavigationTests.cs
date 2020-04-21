namespace Diwen.Aifmd.Tests
{
    using System.Xml;
    using System.Xml.Schema;
    using System.IO;
    using System.Linq;
    using System.Collections.Generic;
    using System.Xml.Linq;
    using System;
    using System.Xml.XPath;
    using System.Text.RegularExpressions;
    using Xunit;

    public class NavigationTests
    {
        [Fact]
        public void ReadWriteWithValidationTest()
        {
            var inputPath = Path.Combine("data", "AIFSample.xml");
            var outputPath = "output.xml";

            var schemas = GetSchemas();

            var cellStructure = ReadCellStructure();

            var inputReport = XDocument.Load(inputPath);
            var success = true;
            inputReport.Validate(schemas, (o, e) =>
                {
                    Console.WriteLine("{0}", e.Message);
                    success = false;
                });

            Assert.True(success);

            var headers = ReadHeaders(inputReport);

            var dataFromXml = ReadReport(inputReport);

            var cellData = CreateCellData(dataFromXml, cellStructure);

            DumpCellData(cellData);

            var dataForXml = CreateOutputData(cellData, cellStructure);

            var outputReport = WriteReport(dataForXml);
            WriteHeaders(outputReport, headers);

            outputReport.Save(outputPath);

            outputReport.Validate(schemas, (o, e) =>
                {
                    Console.WriteLine("{0}", e.Message);
                    success = false;
                });
            Assert.True(success);
        }

        private static XmlSchemaSet GetSchemas()
        {
            var schemas = new XmlSchemaSet();
            Directory.GetFiles("data", "*.xsd").ToList().ForEach(s => schemas.Add(null, s));
            schemas.Compile();
            return schemas;
        }

        private static Dictionary<string, string> ReadReport(XDocument report)
        {
            return report.Descendants().Where(d => !d.Descendants().Any()).ToDictionary(l => GetPath(l), l => l.Value);
        }

        private static Dictionary<string, string> ReadHeaders(XDocument report)
        {
            return report.Root.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value);
        }

        private static void WriteHeaders(XDocument report, Dictionary<string, string> headers)
        {
            headers.ToList().ForEach(a => report.Root.SetAttributeValue(a.Key, a.Value));
        }

        private static XDocument WriteReport(Dictionary<string, string> data)
        {
            var report = new XDocument();
            foreach (var item in data)
            {
                var node = ElementFromPath(report, item.Key);
                node.Value = item.Value;
            }
            return report;
        }

        public static Dictionary<string, string> CreateOutputData(List<CellDatabaseCell> celldata, List<CellStructureCell> cellStructure)
        {
            var zLookUp = CreateZLookup(celldata);
            return CreateRawData(celldata, cellStructure, zLookUp);
        }

        public static void DumpCellData(List<CellDatabaseCell> celldata)
        {
            using (var file = new StreamWriter("CellDatabase.out", false))
            {
                foreach (var item in celldata.Distinct().OrderBy(i => i.ContextKey).ThenBy(i => i.X).ThenBy(i => i.Y).ThenBy(i => i.Z).ThenBy(i => i.RowNumber).ThenBy(i => i.CellValue))
                {
                    file.WriteLine(string.Join(",", item.ContextKey, item.X, item.Y, item.Z, item.RowNumber, item.CellValue));
                }
            }
        }

        public static string GetPath(XElement element)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            var path = element.Name.LocalName;

            if (element.Parent != null)
            {
                int idx;
                var siblings = element.Parent.Elements(element.Name).ToList();
                if (siblings.Count != 1)
                {
                    idx = siblings.IndexOf(element) + 1;
                    if (idx != 0)
                    {
                        path += "[" + idx + "]";
                    }
                }
                path = GetPath(element.Parent) + "." + path;
            }
            return path;
        }

        public static XElement ElementFromPath(XDocument document, string path)
        {
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }

            if (path == null)
            {
                throw new ArgumentNullException("path");
            }

            var parts = path.Split('.');

            var node = document.XPathSelectElement(parts[0]);
            if (node == null)
            {
                document.Add(new XElement(parts[0]));
                node = document.XPathSelectElement(parts[0]);
            }

            for (int i = 1; i < parts.Length; i++)
            {
                var part = parts[i];
                var next = node.XPathSelectElement(part);

                if (next == null)
                {
                    var x = part.IndexOf('[');
                    if (x != -1)
                    {
                        var p = part.Remove(x);
                        do
                        {
                            node.Add(new XElement(p));
                            next = node.XPathSelectElement(part);
                        } while (next == null);
                    }
                    else
                    {
                        node.Add(new XElement(part));
                    }

                    next = node.XPathSelectElement(part);
                }
                node = next;
            }
            return node;
        }

        public static List<CellStructureCell> ReadCellStructure()
        {
            var file = Path.Combine("data", "CellStructure.csv");
            var contents = File.ReadLines(file);
            var result = new List<CellStructureCell>();
            foreach (var line in contents.Skip(1))
            {
                var record = line.Split(',');
                result.Add(new CellStructureCell(record[0], record[1], record[2], XmlConvert.ToBoolean(record[3].ToLower()), record[4]));
            }
            return result;
        }

        public static List<CellDatabaseCell> CreateCellData(Dictionary<string, string> rawData, List<CellStructureCell> cellstructure)
        {
            var zLookup = CreateZLookup(rawData);
            var yLookup = CreateRawData(rawData, cellstructure);
            var celldata = ParseRawData(rawData, zLookup, yLookup);
            AddDefaultValueKeys(celldata, cellstructure, zLookup);
            return celldata;
        }

        private static void AddDefaultValueKeys(List<CellDatabaseCell> celldata, List<CellStructureCell> cellstructure, Dictionary<string, string> zLookup)
        {
            var keys = cellstructure.Where(c => c.IsRowKey && !string.IsNullOrEmpty(c.DefaultValue)).ToList();
            var x = (string)null;
            var y = (string)null;
            var row = 0;
            var cancellations = new string[] { "AIFC", "AIFMC" };
            foreach (var key in keys)
            {
                var value = key.DefaultValue;
                int i;
                if (!(int.TryParse(value, out i) && key.ContextKey.EndsWith("[" + value + "]")))
                {
                    var contextKey = key.ContextKey + "|" + key.MemberCode;
                    foreach (var z in zLookup.Values.Where(v => !cancellations.Contains(v)))
                    {
                        celldata.Add(new CellDatabaseCell(contextKey, x, y, z, value, row));
                    }
                }
            }
        }

        private static List<CellDatabaseCell> ParseRawData(Dictionary<string, string> rawData, Dictionary<string, string> zLookup, Dictionary<string, string> yLookup)
        {
            var celldata = new List<CellDatabaseCell>();
            foreach (var item in rawData)
            {
                try
                {
                    var idx = item.Key.LastIndexOf('.');
                    var memberCode = item.Key.Substring(idx + 1);
                    var contextKey = item.Key.Remove(idx);
                    var x = (string)null;
                    var yResult = LookupAxisValue(contextKey, yLookup);
                    var y = yResult.Value;
                    var z = LookupAxisValue(contextKey, zLookup).Value;
                    var cellValue = item.Value;
                    var rowNumber = 0;
                    var parts = Regex.Matches(contextKey, @"\[[^[#]+\]");

                    if ((string.IsNullOrEmpty(z) && parts.Count == 1) || parts.Count == 2)
                    {
                        rowNumber = int.Parse(parts[parts.Count - 1].Value.TrimStart('[').TrimEnd(']'));
                    }

                    foreach (Match part in parts)
                    {
                        contextKey = contextKey.Replace(part.Value, string.Empty);
                    }

                    if ((y ?? "").StartsWith("#"))
                    {
                        contextKey += "[" + y.TrimStart('#') + "]";
                        rowNumber = 0;
                        y = null;
                    }

                    celldata.Add(new CellDatabaseCell(contextKey + "|" + memberCode, x, y, z, cellValue, rowNumber));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }

            return celldata.Distinct().ToList();
        }

        public static KeyValuePair<string, string> LookupAxisValue(string contextKey, Dictionary<string, string> lookup)
        {
            var match = lookup.FirstOrDefault(l => contextKey.StartsWith(l.Key));
            if (match.Key == null)
            {
                if (contextKey.EndsWith("]") && contextKey.Split('.').Length > 2)
                {
                    match = lookup.FirstOrDefault(l => l.Key.Split('.').Length > 2 && l.Key.StartsWith(contextKey));
                }

            }
            return match;
        }

        public struct RawDataItem
        {
            public string ContextKey { get; }

            public string CName { get; }

            public string Value { get; }

            public RawDataItem(string contextKey, string cname, string value)
            {
                this.ContextKey = contextKey;
                this.CName = cname;
                this.Value = value;
            }

            public override string ToString()
            {
                return string.Format("[RawDataItem: ContextKey={0}, CName={1}, Value={2}]", ContextKey, CName, Value);
            }
        }

        public static Dictionary<string, string> CreateRawData(
            List<CellDatabaseCell> data,
            List<CellStructureCell> cellstructure,
            Dictionary<string, string> zLookup)
        {
            var rowKeys = cellstructure.Where(c => c.IsRowKey && string.IsNullOrEmpty(c.DefaultValue)).ToList();
            var result = new Dictionary<string, string>();
            var rawData = new List<RawDataItem>();
            foreach (var z in zLookup)
            {
                var zData = data.Where(c => c.Z == z.Value).ToList();
                var zParts = z.Key.Split('.');
                var baz = new Dictionary<string, List<string>>();

                #region OpenY    
                var zOpen = zData.Where(c => !string.IsNullOrEmpty(c.Y)).ToList();

                foreach (var key in rowKeys)
                {
                    var keyCells = zOpen.Where(c => c.ContextKey == key.ContextKey + "|" + key.MemberCode).ToList();
                    var idx = 0;
                    foreach (var keyCell in keyCells)
                    {
                        idx++;
                        var parts = key.ContextKey.Split('.');

                        for (int i = 0; i < zParts.Length; i++)
                        {
                            parts[i] = zParts[i];
                        }

                        var foo = string.Join(".", parts) + "[" + idx.ToString() + "]";
                        var foos = foo.Split('.');
                        var datapoints = zOpen.Where(d => d.Y == keyCell.CellValue && d.ContextKey.StartsWith(key.ContextKey)).ToList();
                        foreach (var datapoint in datapoints)
                        {
                            var contextKey = datapoint.ContextKey;
                            var p = contextKey.LastIndexOf('|');
                            var memberCode = contextKey.Substring(p + 1);
                            contextKey = contextKey.Remove(p);

                            var cname = cellstructure.FirstOrDefault(c => c.ContextKey == contextKey && c.MemberCode == memberCode).CName;

                            var ctxParts = contextKey.Split('.');

                            for (int i = 0; i < foos.Length; i++)
                            {
                                ctxParts[i] = foos[i];
                            }
                            contextKey = string.Join(".", ctxParts) + "." + memberCode;

                            var item = new RawDataItem(contextKey, cname, datapoint.CellValue);
                            rawData.Add(item);

                        }
                    }
                }
                #endregion

                #region ClosedY
                var zClosed = zData.Where(c => string.IsNullOrEmpty(c.Y)).ToList();
                foreach (var datapoint in zClosed)
                {
                    var contextKey = datapoint.ContextKey;

                    var p = contextKey.LastIndexOf('|');
                    var memberCode = contextKey.Substring(p + 1);
                    contextKey = contextKey.Remove(p);

                    var cname = cellstructure.FirstOrDefault(c => c.ContextKey == contextKey && c.MemberCode == memberCode).CName;
                    var bar = Regex.Match(contextKey, @"\[[^[]+\]");
                    //var idx = 0;

                    if (bar.Success)
                    { // "Opened" case with string key in contextvalue
                        int alice;
                        var kk = bar.Captures[0].Value;
                        var k = kk.TrimStart('[').TrimEnd(']');


                        var ctxParts = contextKey.Remove(contextKey.LastIndexOf('[')).Split('.');

                        var xk = cellstructure.FirstOrDefault(x => x.IsRowKey && contextKey.StartsWith(x.ContextKey.Replace(kk, string.Empty)));

                        if (xk.ContextKey != null)
                        {
                            var xkParts = xk.ContextKey.Split('.');
                            for (int i = 0; i < xkParts.Length; i++)
                            {
                                ctxParts[i] = xkParts[i];
                            }
                        }

                        for (int i = 0; i < zParts.Length; i++)
                        {
                            ctxParts[i] = zParts[i];
                        }
                        contextKey = string.Join(".", ctxParts);

                        contextKey += "." + memberCode;

                        if (!int.TryParse(k, out alice))
                        {
                            List<string> a0;
                            if (!baz.TryGetValue(contextKey, out a0))
                            {
                                a0 = new List<string>();
                                a0.Add(k);
                                baz.Add(contextKey, a0);
                            }

                            alice = a0.IndexOf(k) + 1;
                            if (alice == 0)
                            {
                                a0.Add(k);
                                alice = a0.Count;
                            }
                            contextKey = contextKey.Replace(kk, "[" + alice.ToString() + "]");
                        }

                        var item = new RawDataItem(contextKey, cname, datapoint.CellValue);
                        rawData.Add(item);
                    }
                    else
                    {
                        // Normal x*y case
                        var ctxParts = contextKey.Split('.');
                        for (int i = 0; i < zParts.Length; i++)
                        {
                            ctxParts[i] = zParts[i];
                        }
                        contextKey = string.Join(".", ctxParts) + "." + memberCode;

                        var item = new RawDataItem(contextKey, cname, datapoint.CellValue);
                        rawData.Add(item);
                    }
                }
                #endregion
            }
            foreach (var item in rawData.Distinct().OrderBy(d => d.CName))
            {
                if (!result.ContainsKey(item.ContextKey))
                {
                    result.Add(item.ContextKey, item.Value);
                }
                else
                {
                    Console.WriteLine(item);
                }

            }

            return result;
        }

        public static Dictionary<string, string> CreateRawData(Dictionary<string, string> data, List<CellStructureCell> cellstructure)
        {
            var rowKeys = cellstructure.Where(c => c.IsRowKey).ToList();
            var yLookup = new Dictionary<string, string>();

            foreach (var item in data)
            {
                var keyWithoutBrackets = Regex.Replace(item.Key, @"\[[^[]+\]", string.Empty);
                var match = rowKeys.FirstOrDefault(k => keyWithoutBrackets == k.ContextKey + "." + k.MemberCode);
                var closedY = false;
                if (match.ContextKey == null)
                {
                    var idx = keyWithoutBrackets.LastIndexOf('.');
                    keyWithoutBrackets = keyWithoutBrackets.Remove(idx) + "[" + item.Value + "]" + keyWithoutBrackets.Substring(idx);
                    match = rowKeys.FirstOrDefault(k => keyWithoutBrackets == k.ContextKey + "." + k.MemberCode);
                    closedY = (match.ContextKey != null);
                }

                if (match.ContextKey != null)
                {
                    var yKey = item.Key.Remove(item.Key.LastIndexOf('.'));
                    var yValue = item.Value;
                    if (closedY)
                    {
                        yValue = '#' + yValue;
                    }
                    yLookup.Add(yKey, yValue);
                }


            }
            return yLookup;
        }

        public static Dictionary<string, string> CreateZLookup(List<CellDatabaseCell> data)
        {
            string pattern = @"AIFReportingInfo.AIFRecordInfo|AIFNationalCode";
            var zLookup = new Dictionary<string, string>();
            var zValues = data.Where(d => d.ContextKey == pattern).Select(d => d.CellValue);
            var idx = 0;
            foreach (var zValue in zValues)
            {
                idx++;
                var zKey = "AIFReportingInfo.AIFRecordInfo[" + idx.ToString() + "]";
                zLookup.Add(zKey, zValue);
            }
            return zLookup;
        }

        public static Dictionary<string, string> CreateZLookup(Dictionary<string, string> data)
        {
            string pattern = @"^AIFReportingInfo\.AIFRecordInfo\[\d+\]\.AIFNationalCode|AIFMReportingInfo\.AIFMRecordInfo\[\d+\]\.AIFMNationalCode$";
            var zLookup = new Dictionary<string, string>();
            var zValues = data.Where(d => Regex.IsMatch(d.Key, pattern));
            foreach (var zValue in zValues)
            {
                var zKey = zValue.Key.Remove(zValue.Key.IndexOf(']') + 1);
                zLookup.Add(zKey, zValue.Value);
            }
            zLookup.Add("AIFReportingInfo.CancellationAIFRecordInfo", "AIFC");
            zLookup.Add("AIFMReportingInfo.CancellationAIFMRecordInfo", "AIFMC");
            return zLookup;
        }

        public struct CellStructureCell
        {
            public string CName { get; }

            public string ContextKey { get; }

            public string MemberCode { get; }

            public bool IsRowKey { get; }

            public string DefaultValue { get; }

            public CellStructureCell(string cname, string contextKey, string memberCode, bool isRowKey, string defaultValue)
            {
                this.CName = cname;
                this.ContextKey = contextKey;
                this.MemberCode = memberCode;
                this.IsRowKey = isRowKey;
                this.DefaultValue = defaultValue;
            }
        }

        public struct CellDatabaseCell
        {
            public string ContextKey { get; }

            public string X { get; }

            public string Y { get; }

            public string Z { get; }

            public string CellValue { get; }

            public int RowNumber { get; }

            public CellDatabaseCell(string contextKey, string x, string y, string z, string cellValue, int rownumber)
            {
                this.ContextKey = contextKey;
                this.X = x;
                this.Y = y;
                this.Z = z;
                this.CellValue = cellValue;
                this.RowNumber = rownumber;
            }
        }
    }
}