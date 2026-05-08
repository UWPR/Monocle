using Monocle.Data;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Monocle.File
{
    public class MzXmlReader : IScanReader
    {
        private XmlReader Reader;
        
        private ScanFileHeader Header = new ScanFileHeader();

        private string FilePath;

        private static readonly Dictionary<string, Action<Scan, string>> ScanSetters = new Dictionary<string, Action<Scan, string>>()
        {
            { "num",               (s, v) => { if (int.TryParse(v, out int i))    s.ScanNumber = i; } },
            { "msLevel",           (s, v) => { if (int.TryParse(v, out int i))    s.MsOrder = i; } },
            { "scanEvent",         (s, v) => { if (int.TryParse(v, out int i))    s.ScanEvent = i; } },
            { "masterIndex",       (s, v) => { if (int.TryParse(v, out int i))    s.MasterIndex = i; } },
            { "peaksCount",        (s, v) => { if (int.TryParse(v, out int i))    s.PeakCount = i; } },
            { "ionInjectionTime",  (s, v) => { if (double.TryParse(v, out double d)) s.IonInjectionTime = d; } },
            { "elapsedScanTime",   (s, v) => { if (double.TryParse(v, out double d)) s.ElapsedScanTime = d; } },
            { "scanType",          (s, v) => s.ScanType = v },
            { "filterLine",        (s, v) => s.FilterLine = v },
            { "description",       (s, v) => s.Description = v },
            { "startMz",           (s, v) => { if (double.TryParse(v, out double d)) s.StartMz = d; } },
            { "endMz",             (s, v) => { if (double.TryParse(v, out double d)) s.EndMz = d; } },
            { "lowMz",             (s, v) => { if (double.TryParse(v, out double d)) s.LowestMz = d; } },
            { "highMz",            (s, v) => { if (double.TryParse(v, out double d)) s.HighestMz = d; } },
            { "basePeakMz",        (s, v) => { if (double.TryParse(v, out double d)) s.BasePeakMz = d; } },
            { "basePeakIntensity", (s, v) => { if (double.TryParse(v, out double d)) s.BasePeakIntensity = d; } },
            { "faimsCv",           (s, v) => { if (double.TryParse(v, out double d)) s.FaimsCV = d; } },
            { "totIonCurrent",     (s, v) => { if (double.TryParse(v, out double d)) s.TotalIonCurrent = d; } },
            { "collisionEnergy",   (s, v) => { if (double.TryParse(v, out double d)) s.CollisionEnergy = d; } },
            { "precursorScanNum",  (s, v) => { if (int.TryParse(v, out int i))    s.PrecursorMasterScanNumber = i; } },
            { "activationMethod",  (s, v) => s.PrecursorActivationMethod = v },
        };

        /// <summary>
        /// Open new fileStream to mzXML file.
        /// </summary>
        /// <param name="path"></param>
        public void Open(string path, ScanReaderOptions options)
        {
            if (!System.IO.File.Exists(path)) {
                throw new IOException("File not found: " + path);
            }
            FilePath = path;
            Reader = XmlReader.Create(FilePath);
            ReadHeader();
        }

        /// <summary>
        /// Returns header information from the mzXML file.
        /// </summary>
        /// <returns>An instance of the ScanFileHeader class</returns>
        public ScanFileHeader GetHeader() {
            return Header;
        }

        /// <summary>
        /// Dispose of the reader when reading multiple files.
        /// </summary>
        public void Close()
        {
            Reader.Dispose();
        }

        /// <summary>
        /// Open the given file and import scans into the reader.
        /// </summary>
        /// <returns></returns>
        public IEnumerator GetEnumerator() {
            // Reset to beginning of document.
            Reader = XmlReader.Create(FilePath);
            Scan scan = null;
            while(Reader.Read()) {
                switch (Reader.NodeType) {
                    case XmlNodeType.Element:

                        if (scan != null && (Reader.Name == "scan" || Reader.Name == "index")) {
                            // mzXML can have nested scans, so returning scans here.
                            yield return scan;
                        }

                        if (Reader.Name == "scan") {
                            scan = new Scan();
                            while (Reader.MoveToNextAttribute()) {
                                SetAttribute(scan, Reader.Name, Reader.Value);
                                if (Reader.Name == "filterLine") {
                                    int spacePos = scan.FilterLine.IndexOf(' ');
                                    if (spacePos > 0) {
                                        // Setting detector after setting filterLine in SetAttribute()
                                        scan.DetectorType = scan.FilterLine.Substring(0, spacePos).ToUpper();
                                    }
                                }
                            }
                        }
                        if (Reader.Name == "peaks" && scan != null) {
                            scan.Centroids = ReadPeaks(Reader.ReadElementContentAsString(), scan.PeakCount);
                        }
                        else if (Reader.Name == "precursorMz" && scan != null) {
                            var precursor = new Precursor();
                            while (Reader.MoveToNextAttribute()) {
                                if (Reader.Name == "precursorCharge") {
                                    precursor.Charge = int.Parse(Reader.Value);
                                }
                                else if(Reader.Name == "precursorIntensity") {
                                    precursor.Intensity = double.Parse(Reader.Value);
                                }
                                else if(Reader.Name == "isolationWidth") {
                                    precursor.IsolationWidth = double.Parse(Reader.Value);
                                }
                                else if(Reader.Name == "isolationMz") {
                                    precursor.IsolationMz = double.Parse(Reader.Value);
                                }
                                else {
                                    SetAttribute(scan, Reader.Name, Reader.Value);
                                }
                            }
                            Reader.MoveToContent();
                            precursor.Mz = double.Parse(Reader.ReadElementContentAsString());
                            precursor.OriginalMz = precursor.Mz;
                            precursor.OriginalCharge = precursor.Charge;
                            scan.Precursors.Add(precursor);
                        }
                        break;
                    default:
                    break;
                }
            }
        }

        /// <summary>
        /// Check and set attribute based on attributes dictionary
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="value"></param>
        public void SetAttribute(Scan scan, string attribute, string value)
        {
            if (attribute == "retentionTime") {
                scan.RetentionTime = ParseRetentionTime(value);
                return;
            }
            if (ScanSetters.TryGetValue(attribute, out var setter))
            {
                setter(scan, value);
            }
        }

        private void ReadHeader() {
            Header.FileName = Path.GetFileName(FilePath);
            while(Reader.Read()) {
                switch (Reader.NodeType) {
                    case XmlNodeType.Element:
                        if (Reader.Name == "msRun") {
                            while (Reader.MoveToNextAttribute()) {
                                switch(Reader.Name) {
                                    case "scanCount":
                                        Header.ScanCount = int.Parse(Reader.Value);
                                        break;
                                    case "startTime":
                                        Header.StartTime = ParseRetentionTime(Reader.Value);
                                        break;
                                    case "endTime":
                                        Header.EndTime = ParseRetentionTime(Reader.Value);
                                        break;
                                    default:
                                        break;
                                }
                            }
                        }
                        else if (Reader.Name == "msManufacturer") {
                            while (Reader.MoveToNextAttribute()) {
                                if (Reader.Name == "value") {
                                    Header.InstrumentManufacturer = Reader.Value;
                                }
                            }
                        }
                        else if (Reader.Name == "msModel") {
                            while (Reader.MoveToNextAttribute()) {
                                if (Reader.Name == "value") {
                                    Header.InstrumentModel = Reader.Value;
                                }
                            }
                        }
                        else if (Reader.Name == "scan") {
                            // Gone too far.
                            Reader.Dispose();
                            Reader = XmlReader.Create(FilePath);
                            return;
                        }
                        break;
                    case XmlNodeType.EndElement:
                        if (Reader.Name == "msInstrument") {
                            return;
                        }
                        break;
                    default:
                    break;
                }
            }
        }

        /// <summary>
        /// Read mzXML peaks property
        /// </summary>
        /// <param name="str"></param>
        /// <param name="peakCount"></param>
        /// <returns></returns>
        private List<Centroid> ReadPeaks(string str, int peakCount) {
            var peaks = new List<Centroid>(peakCount);
            if (str == "AAAAAAAAAAA=")
            {
                return peaks;
            }
            byte[] bytes = Convert.FromBase64String(str);
            for (int i = 0; i < peakCount; ++i)
            {
                float mz        = BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i * 8));
                float intensity = BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i * 8 + 4));
                peaks.Add(new Centroid(mz, intensity));
            }
            return peaks;
        }

        private void Cleanup() {
            if (Reader != null) {
                ((IDisposable)Reader).Dispose();
            }
        }

        /// <summary>
        /// Converts retention time text into the number of minutes.
        /// 
        /// Input text is of the type xsd:duration
        /// </summary>
        /// <param name="text">Input, e.g. "PT2530.331S"</param>
        /// <returns>Number of Minutes</returns>
        private double ParseRetentionTime(string text) {
            try {
                if (text.StartsWith("PT")) {
                    var span = XmlConvert.ToTimeSpan(text);
                    return span.TotalMinutes;
                }

                return float.Parse(text);
            } catch (FormatException) {
                return 0;
            }
        }
   }
}
