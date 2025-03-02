﻿#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
using Hl7.Cql.Compiler;
using Hl7.Cql.Fhir;
using Hl7.Cql.Packaging;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text.Json;

namespace Hl7.Cql.Packager
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var config = new ConfigurationBuilder()
                .AddCommandLine(args)
                .Build();
            if (args.Length == 0 || config["?"] != null || config["h"] != null || config["help"] != null)
                return ShowHelp();

            var elmArg = config["elm"];
            if (elmArg == null)
                return ShowHelp();
            var elmDir = new DirectoryInfo(elmArg);
            if (!elmDir.Exists)
            {
                Console.Error.WriteLine($"-elm: path {elmArg} does not exist.");
                return -1;
            }
            var cqlArg = config["cql"];

            if (cqlArg == null)
                return ShowHelp();
            var cqlDir = new DirectoryInfo(cqlArg);
            if (!cqlDir.Exists)
            {
                Console.Error.WriteLine($"-cql: path {cqlArg} does not exist.");
                return -1;
            }



            var dArg = config["d"];
            bool debug = false;
            if (dArg != null && !bool.TryParse(dArg, out debug))
            {
                Console.Error.WriteLine($"-d: expected true|false, got {dArg}");
                return -1;
            }

            DirectoryInfo? csDir = null;
            var csArg = config["cs"];
            if (csArg != null)
            {
                csDir = new DirectoryInfo(csArg);
                if (!csDir.Exists)
                {
                    EnsureDirectory(csDir);
                }
            }

            var fArg = config["f"];
            bool force = false;
            if (fArg != null && !bool.TryParse(fArg, out force))
            {
                Console.Error.WriteLine($"-f: expected true|false, got {fArg}");
                return -1;
            }

            DirectoryInfo? fhirDir = null;
            var fhirArg = config["fhir"];
            if (fhirArg != null)
            {
                fhirDir = new DirectoryInfo(fhirArg);
                if (!fhirDir.Exists)
                {
                    EnsureDirectory(fhirDir);
                }
            }
            Package(elmDir, cqlDir, csDir, fhirDir);
            return 0;
        }

        public static void Package(DirectoryInfo elmDir, DirectoryInfo cqlDir, DirectoryInfo? csDir, DirectoryInfo? fhirDir)
        {
            var logLevel = LogLevel.Trace;
            var logFactory = LoggerFactory
                .Create(logging =>
                {
                    logging.AddFilter(level => level >= logLevel);
                    logging.AddConsole(console =>
                    {
                        console.LogToStandardErrorThreshold = LogLevel.Error;
                    });
                    var logFile = Path.Combine(".", "build.txt");
#pragma warning disable CA1305 // Specify IFormatProvider
                    Log.Logger = new LoggerConfiguration()
                      .Enrich.FromLogContext()
                      .WriteTo
                      .File(logFile)
                      .CreateLogger();
#pragma warning restore CA1305 // Specify IFormatProvider
                    logging.AddSerilog();
                });

            var packagerLogger = logFactory.CreateLogger<LibraryPackager>();
            var packages = LibraryPackager.LoadLibraries(elmDir);
            var graph = Elm.Library.GetIncludedLibraries(packages.Values);
            var typeResolver = new FhirTypeResolver(ModelInfo.ModelInspector);
            var cliLogger = logFactory.CreateLogger("CLI");

            var packager = new LibraryPackager();
            var resources = packager.PackageResources(elmDir,
                cqlDir,
                graph,
                typeResolver,
                new CqlOperatorsBinding(typeResolver, FhirTypeConverter.Create(ModelInfo.ModelInspector)),
                new TypeManager(typeResolver),
                CanonicalUri,
                logFactory);

            var options = new JsonSerializerOptions()
                .ForFhir(typeof(Resource).Assembly)
                .Pretty();
            if (fhirDir != null)
            {

                cliLogger.LogInformation($"Writing FHIR resources to {fhirDir.FullName}");

                foreach (var resource in resources)
                {
                    var file = new FileInfo(Path.Combine(fhirDir.FullName, $"{resource.Id}.json"));
                    cliLogger.LogInformation($"Writing {file.FullName}");
                    using var fs = new FileStream(file.FullName, FileMode.Create, FileAccess.Write, FileShare.Read);
                    JsonSerializer.Serialize(fs, resource, options);
                }
            }
            if (csDir != null)
            {
                cliLogger.LogInformation($"Writing C# source files to {csDir.FullName}");
                // Write out the C# source code to the desired output location
                foreach (var resource in resources)
                {
                    if (resource is Binary binary)
                    {
                        if (binary.ContentType == "text/plain")
                        {
                            var bytes = binary.Data;
                            DirectoryInfo? sourceDir = null;
                            if (binary.Id.StartsWith("Tuple_"))
                            {
                                sourceDir = new(Path.Combine(csDir.FullName, "Tuples"));
                            }
                            else
                            {
                                sourceDir = new(csDir.FullName);
                            }
                            EnsureDirectory(sourceDir);
                            var filePath = Path.Combine(sourceDir.FullName, $"{binary.Id}.cs");
                            cliLogger.LogInformation($"Writing {filePath}");
                            File.WriteAllBytes(filePath, bytes);
                        }
                    }
                    else if (resource is Library library && library.Content != null)
                    {
                        var textPlain = library.Content
                            .SingleOrDefault(c => c.ContentType == "text/plain");
                        if (textPlain != null)
                        {
                            var bytes = textPlain.Data;
                            var sourceFilePath = Path.Combine(csDir.FullName, $"{library.Id}.cs");
                            File.WriteAllBytes(sourceFilePath, bytes);
                        }
                    }
                }
            }
        }

        private static string CanonicalUri(Resource resource)
        {
            if (string.IsNullOrWhiteSpace(resource.Id))
                throw new ArgumentException("Resource must have an id", nameof(resource));
            var path = $"#/{resource.TypeName}/{resource.Id}";
            return path;
        }

        private static void EnsureDirectory(DirectoryInfo directory, int timeoutMs = 5000)
        {
            var now = DateTime.Now;
            var loop = true;
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            while (!directory.Exists && loop)
            {
                directory.Create();
                directory.Refresh();
                if (DateTime.Now.Subtract(now) > timeout)
                    throw new InvalidOperationException($"Unable to create directory {directory.FullName} after {timeout}");
            }
        }

        private static int ShowHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Measure Packager");
            Console.WriteLine();
            Console.WriteLine($"\t--elm <directory>\tLibrary root path");
            Console.WriteLine($"\t--cql <directory>\tCQL root path");
            Console.WriteLine($"\t[--fhir] <file>\tResource location, either file name or directory");
            Console.WriteLine($"\t[--cs] <file>\tC# output location, either file name or directory");
            Console.WriteLine($"\t[--d] true|false\t\tProduce as a debug assembly");
            Console.WriteLine($"\t[--f] true|false\tIf output file already exists, overwrite");
            Console.WriteLine();
            return -1;
        }
    }
}