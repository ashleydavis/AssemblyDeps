using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AssemblyDeps
{
    public class Config
    {
        /// <summary>
        /// List of paths that contain dll files to check.
        /// </summary>
        public List<string> Paths = new List<string>();

        /// <summary>
        /// List of paths where system dlls can be found.
        /// </summary>
        public List<string> SystemPaths = new List<string>();

        /// <summary>
        /// List of regular expressions that define dlls to ignore.
        /// </summary>
        public List<string> ExcludedDlls = new List<string>();
    }

    /// <summary>
    /// Records info about a particular dll.
    /// </summary>
    public class DllInfo
    {
        public string name = null;
        public List<string> locations = new List<string>();
        public Dictionary<string, DllInfo> children = new Dictionary<string, DllInfo>();
        public bool missing = false;
        public bool loadFailed = false;
    }

    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Bad arguments!");
                Console.WriteLine("Usage:");
                Console.WriteLine("AssemblyDeps.exe <config-filename>");
                return 1;
            }

            var configFilePath = args[0];
            if (!File.Exists(configFilePath))
            {
                Console.Error.WriteLine("Specified config file doesn't exist: " + configFilePath);
                return 1;
            }

            try
            {
                var config = LoadConfig(configFilePath);
                if (config == null)
                {
                    Console.Error.WriteLine("Failed to load config file: " + configFilePath);
                    return 1;
                }

                if (config.Paths.Count == 0)
                {
                    Console.Error.WriteLine("No 'Paths' specified in " + configFilePath);
                    return 1;
                }

                var allDlls = LocateDlls(config);
                var rootDlls = DetermineDeps(allDlls, config);

                Console.WriteLine("=== Assembly Dependencies ===");
                OutputDetails(rootDlls, config, new HashSet<string>());

                Console.WriteLine();

                Console.WriteLine("=== System DLLs ===");
                int numSystemDlls = OutputSystemDlls(rootDlls, new HashSet<string>(), config);
                if (numSystemDlls == 0)
                {
                    Console.WriteLine("none");
                }
                Console.WriteLine();

                Console.WriteLine("=== Missing DLLs ===");
                int numMissingDlls = OutputMissingDlls(rootDlls, new HashSet<string>(), config);
                if (numMissingDlls == 0)
                {
                    Console.WriteLine("none");
                }
                Console.WriteLine();

                Console.WriteLine("=== Failed to load ===");
                int numFailedToLoadDlls = OutputFailedToLoadDlls(allDlls.Values, config);
                if (numFailedToLoadDlls == 0)
                {
                    Console.WriteLine("none");
                }
                Console.WriteLine();

                Console.WriteLine("=== Duplicate DLLs ===");
                int numDuplicateDlls = OutputDuplicateDlls(allDlls.Values);
                if (numDuplicateDlls == 0)
                {
                    Console.WriteLine("none");
                }
                Console.WriteLine();

                Console.WriteLine("=== Summary ===");
                Console.WriteLine(numMissingDlls + " missing dlls.");
                Console.WriteLine(numFailedToLoadDlls + " dlls failed to load.");
                Console.WriteLine(numDuplicateDlls + " duplicate dlls.");

                var failed = numDuplicateDlls > 0 || numMissingDlls > 0 || numFailedToLoadDlls > 0;
                Console.WriteLine();
                Console.WriteLine((failed ? "Failed" : "Passed") + " assembly dependency validation.");

                if (failed)
                {
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Exception Ocurred:\n" + ex.ToString());
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Scan 'paths' from config and generate a dictionary of dls.
        /// Duplicate dlls in separate paths are noted.
        /// </summary>
        private static Dictionary<string, DllInfo> LocateDlls(Config config)
        {
            Dictionary<string, DllInfo> dlls = new Dictionary<string, DllInfo>();

            foreach (var path in config.Paths)
            {
                var dllPaths = Directory.GetFiles(path, "*.dll");

                foreach (var dllPath in dllPaths)
                {
                    var dllFileName = Path.GetFileName(dllPath);
                    DllInfo dllInfo = null;
                    if (!dlls.TryGetValue(dllFileName.ToLower(), out dllInfo))
                    {
                        dllInfo = new DllInfo()
                        {
                            name = dllFileName
                        };

                        dlls[dllFileName.ToLower()] = dllInfo;
                    }

                    dllInfo.locations.Add(dllPath);
                }
            }

            return dlls;
        }

        /// <summary>
        /// Returns true if the dll name is excluded in the config.
        /// </summary>
        private static bool IsExcluded(string dllName, Config config)
        {
            foreach (var excluded in config.ExcludedDlls)
            {
                if ((new Regex(excluded)).IsMatch(dllName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Load a dll, if it can't be loaded record it.
        /// </summary>
        private static Assembly LoadDll(DllInfo dllInfo, string location)
        {
            var fullLocation = Path.GetFullPath(location);

            Assembly a;
            try            
            {
                a = Assembly.LoadFile(fullLocation);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load dll: " + location);
                Console.WriteLine("Full path: " + fullLocation);
                Console.WriteLine("Exception Occurred: ");
                Console.WriteLine(ex.ToString());
                
                dllInfo.loadFailed = true;
                return null;
            }

            return a;
        }

        /// <summary>
        /// Determine and note the dependency hierarchy between dlls, return the root dlls in the hierarchy.
        /// </summary>
        private static IEnumerable<DllInfo> DetermineDeps(Dictionary<string, DllInfo> allDlls, Config config)
        {
            Dictionary<string, DllInfo> rootDlls = new Dictionary<string, DllInfo>(allDlls);

            foreach (var dll in allDlls.Values)
            {
                var a = LoadDll(dll, dll.locations[0]);
                if (a == null)
                {
                    continue;
                }

                foreach (var assemblyName in a.GetReferencedAssemblies())
                {
                    var refDllName = assemblyName.Name + ".dll";

                    // This isn't a root dll, thats for sure.
                    rootDlls.Remove(refDllName);

                    DllInfo refDllInfo = null;
                    if (allDlls.TryGetValue(refDllName.ToLower(), out refDllInfo))
                    {
                        // Dll accounted for, move it to children.
                        dll.children[refDllName] = refDllInfo;
                    }
                    else
                    {
                        // Dll not found in specified paths.
                        var refDll = new DllInfo()
                        {
                            name = refDllName,
                            missing = true,
                        };

                        dll.children[refDllName.ToLower()] = refDll;
                    }
                }
              
            }

            return rootDlls.Values;
        }

        /// <summary>
        /// Output the dlls of the hierarchy of dll dependencies.
        /// </summary>
        private static void OutputDetails(IEnumerable<DllInfo> dlls, Config config, HashSet<string> dllsOutput, int indent = 0)
        {
            foreach (var dll in dlls.OrderBy(d => d.name))
            {
                Console.Write(new string(' ', indent * 4));
                Console.Write(dll.name);

                if (dll.missing)
                {
                    string location;
                    if (!IsExcluded(dll.name, config) &&
                        !FindDll(dll.name, out location, config))
                    {
                        Console.Write(" - missing!");
                    }
                }

                if (dll.loadFailed)
                {
                    Console.Write(" - failed to load!");
                }

                Console.WriteLine();

                if (!dllsOutput.Contains(dll.name)) // Only recurse dependencies for dlls not yet seen.
                {
                    dllsOutput.Add(dll.name);

                    OutputDetails(dll.children.Values, config, dllsOutput, indent + 1);
                }
            }
        }

        /// <summary>
        /// Output dlls that are missing.
        /// </summary>
        private static int OutputSystemDlls(IEnumerable<DllInfo> dlls, HashSet<string> dllsOutput, Config config)
        {
            int num = 0;

            foreach (var dll in dlls)
            {
                if (dll.missing)
                {
                    if (!dllsOutput.Contains(dll.name))
                    {
                        dllsOutput.Add(dll.name);

                        string location;
                        if (FindDll(dll.name, out location, config))
                        {
                            Console.WriteLine(dll.name + " => " + location);
                            ++num;
                        }
                    }
                }

                num += OutputSystemDlls(dll.children.Values, dllsOutput, config);
            }

            return num;
        }

        /// <summary>
        /// Output dlls that are missing.
        /// </summary>
        private static int OutputMissingDlls(IEnumerable<DllInfo> dlls, HashSet<string> dllsOutput, Config config)
        {
            int num = 0;

            foreach (var dll in dlls)
            {
                if (dll.missing)
                {
                    if (!dllsOutput.Contains(dll.name))
                    {
                        dllsOutput.Add(dll.name);

                        string location;
                        if (FindDll(dll.name, out location, config))
                        {
                            // Not really missing.
                        }
                        else if (!IsExcluded(dll.name, config))
                        {
                            Console.WriteLine(dll.name);
                            ++num;
                        }
                    }
                }

                num += OutputMissingDlls(dll.children.Values, dllsOutput, config);
            }

            return num;
        }

        /// <summary>
        /// Find a dll in the specified system paths.
        /// </summary>
        private static bool FindDll(string dllName, out string location, Config config)
        {
            foreach (var systemPath in config.SystemPaths)
            {
                var dllPath = Path.Combine(systemPath, dllName);
                if (File.Exists(dllPath))
                {
                    location = dllPath;
                    return true;
                }
            }

            location = null;
            return false;
        }

        /// <summary>
        /// Output dlls that failed to load.
        /// </summary>
        private static int OutputFailedToLoadDlls(IEnumerable<DllInfo> dlls, Config config)
        {
            int num = 0;

            foreach (var dll in dlls)
            {
                if (dll.loadFailed && 
                    !IsExcluded(dll.name, config))
                {
                    Console.WriteLine(dll.name);
                    ++num;
                }
            }

            return num;
        }

        /// <summary>
        /// Output dlls that are duplicated.
        /// </summary>
        private static int OutputDuplicateDlls(IEnumerable<DllInfo> dlls)
        {
            int num = 0;

            foreach (var dll in dlls)
            {
                if (dll.locations.Count > 1)
                {
                    Console.Write(dll.name);
                    Console.Write(": ");
                    Console.WriteLine(dll.locations.Count);
                    ++num;

                    foreach (var location in dll.locations)
                    {
                        Console.WriteLine("   " + location);
                    }
                }
            }

            return num;
        }

        /// <summary>
        /// Load the config file.
        /// </summary>
        private static Config LoadConfig(string configFilePath)
        {
            try
            {
                return JsonConvert.DeserializeObject<Config>(File.ReadAllText(configFilePath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to open config file: " + configFilePath);
                throw ex;
            }
        }
    }
}
