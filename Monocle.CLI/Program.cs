using Monocle;
using Monocle.Data;
using Monocle.File;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MakeMono
{
    internal class Program
    {
        private static NLog.Logger log = NLog.LogManager.GetCurrentClassLogger();

        static void Main(string[] args)
        {
            var parser = new CliOptionsParser();
            MakeMonoOptions options = parser.Parse(args);

            SetupLogger(options.RunQuiet, options.WriteDebug);

            var inputFiles = ExpandGlobs(options.InputFilePaths).ToList();
            if (inputFiles.Count == 0)
            {
                log.Error("No input files found.");
                Environment.Exit(1);
            }

            if (options.HeaderOnly && inputFiles.Count > 1)
            {
                log.Error("--header-only is supported for single-file input only.");
                Environment.Exit(1);
            }

            string outputOption = options.OutputFilePath.Trim();
            if (inputFiles.Count > 1 && outputOption.Length > 0 && !Directory.Exists(outputOption))
            {
                log.Error("Output directory does not exist: " + outputOption);
                Environment.Exit(1);
            }

            MonocleOptions monocleOptions = new MonocleOptions
            {
                AveragingVector = options.AveragingVector,
                Charge_Detection = options.ChargeDetection,
                Charge_Range = new ChargeRange(options.ChargeRange),
                MS_Level = options.MS_Level,
                Number_Of_Scans_To_Average = options.NumOfScans,
                WriteDebugString = options.WriteDebug,
                OutputFileType = options.OutputFileType,
                ConvertOnly = options.ConvertOnly,
                SkipMono = options.SkipMono,
                ChargeRangeUnknown = new ChargeRange(options.ChargeRangeUnknown),
                ForceCharges = options.ForceCharges,
                UseMostIntense = options.UseMostIntense,
                Ms2Ms3Precursor = options.Ms2Ms3Precursor,
                RawMonoMz = options.RawMonoMz,
                Resolution = options.Resolution,
            };

            var readerOptions = new ScanReaderOptions { Resolution = monocleOptions.Resolution };

            bool multiFile = inputFiles.Count > 1;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.NumConcurrent };
            Parallel.ForEach(inputFiles, parallelOptions, file =>
            {
                string outputPath = ResolveOutputPath(file, outputOption, monocleOptions.OutputFileType, multiFile);
                ProcessFile(file, outputPath, monocleOptions, readerOptions, options.HeaderOnly);
            });
        }

        /// <summary>
        /// Expands glob patterns to matching file paths.
        /// On Linux the shell already expands unquoted globs, so plain paths pass through unchanged.
        /// On Windows (and for quoted patterns on any platform) we expand explicitly.
        /// </summary>
        private static IEnumerable<string> ExpandGlobs(IEnumerable<string> patterns)
        {
            foreach (var pattern in patterns)
            {
                if (!pattern.Contains('*') && !pattern.Contains('?'))
                {
                    yield return pattern;
                    continue;
                }
                string dir = Path.GetDirectoryName(pattern);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                string filePattern = Path.GetFileName(pattern);
                var matches = Directory.GetFiles(dir, filePattern);
                if (matches.Length == 0)
                {
                    log.Warn("No files matched: " + pattern);
                }
                foreach (var match in matches)
                {
                    yield return match;
                }
            }
        }

        /// <summary>
        /// Resolves the output file path for a given input file.
        /// Multi-file: outputOption is an existing directory (already validated) or empty.
        /// Single-file: outputOption may be a directory, a file path, or empty.
        /// </summary>
        private static string ResolveOutputPath(string inputFile, string outputOption, OutputFileType outputFileType, bool multiFile)
        {
            if (outputOption.Length == 0)
            {
                return ScanWriterFactory.MakeTargetFileName(inputFile, outputFileType);
            }
            if (multiFile || Directory.Exists(outputOption))
            {
                string defaultName = Path.GetFileName(ScanWriterFactory.MakeTargetFileName(inputFile, outputFileType));
                return Path.Combine(outputOption, defaultName);
            }
            return outputOption;
        }

        private static void ProcessFile(string file, string outputPath, MonocleOptions monocleOptions, ScanReaderOptions readerOptions, bool headerOnly)
        {
            try
            {
                IScanReader reader = ScanReaderFactory.GetReader(file);
                reader.Open(file, readerOptions);
                var header = reader.GetHeader();
                header.FileName = Path.GetFileName(file);
                header.FilePath = file;

                if (headerOnly)
                {
                    string jsonString = JsonSerializer.Serialize(header);
                    Console.WriteLine(jsonString);
                    reader.Close();
                    return;
                }

                IScanWriter writer = ScanWriterFactory.GetWriter(monocleOptions.OutputFileType);

                if (monocleOptions.ConvertOnly)
                {
                    log.Info("Writing output: " + outputPath);
                    writer.Open(outputPath);
                    writer.WriteHeader(header);
                    foreach (Scan scan in reader)
                    {
                        writer.WriteScan(scan);
                    }
                    writer.Close();
                    reader.Close();
                    log.Info("Done: " + file);
                    return;
                }

                log.Info("Reading scans: " + file);
                List<Scan> scans = new List<Scan>();
                foreach (Scan scan in reader)
                {
                    if (scan.ScanNumber < 1)
                    {
                        continue;
                    }
                    scans.Add(scan);
                }
                reader.Close();

                log.Info("Starting monoisotopic assignment: " + file);
                Monocle.Monocle.Run(ref scans, monocleOptions);

                log.Info("Writing output: " + outputPath);
                writer.Open(outputPath);
                writer.WriteHeader(header);
                foreach (Scan scan in scans)
                {
                    writer.WriteScan(scan);
                }
                writer.Close();
                log.Info("Done: " + file);
            }
            catch (Exception e)
            {
                log.Error("Error processing " + file + ": " + e.Message);
                log.Debug(e.GetType().ToString());
                log.Debug(e.ToString());
            }
        }

        static void SetupLogger(bool quiet, bool debug)
        {
            var config = new NLog.Config.LoggingConfiguration();
            var logconsole = new NLog.Targets.ConsoleTarget("logconsole")
            {
                Layout = "${message}"
            };
            var minLogLevel = NLog.LogLevel.Info;
            if (quiet)
            {
                minLogLevel = NLog.LogLevel.Error;
            }
            else if (debug)
            {
                minLogLevel = NLog.LogLevel.Debug;
            }
            config.AddRule(minLogLevel, NLog.LogLevel.Fatal, logconsole);
            NLog.LogManager.Configuration = config;
        }
    }
}
