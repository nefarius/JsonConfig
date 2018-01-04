//
// Copyright (C) 2012 Timo Dörr
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace JsonConfig
{
    public static class Config
    {
        public delegate void UserConfigFileChangedHandler();

        public static dynamic Default = new ConfigObject();
        public static dynamic User;

        private static dynamic _globalConfig;
        private static FileSystemWatcher _userConfigWatcher;

        static Config()
        {
            // static C'tor, run once to check for compiled/embedded config

            // scan ALL linked assemblies and merge their default configs while
            // giving the entry assembly top priority in merge
            var entryAssembly = Assembly.GetEntryAssembly();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies.Where(assembly => !assembly.Equals(entryAssembly)))
                Default = Merger.Merge(GetDefaultConfig(assembly), Default);
            if (entryAssembly != null)
                Default = Merger.Merge(GetDefaultConfig(entryAssembly), Default);

            // User config (provided through a settings.conf file)
            var executionPath = AppDomain.CurrentDomain.BaseDirectory;
            var userConfigFilename = "settings";

            #region UGLY HACK
            // TODO this is ugly but makes life easier
            // we are run from the IDE, so the settings.conf needs
            // to be searched two levels up
            if (executionPath.EndsWith("/bin/Debug/"))
                executionPath = executionPath.Replace("/bin/Debug", ""); // for Unix-like
            if (executionPath.EndsWith(@"\bin\Debug\"))
                executionPath = executionPath.Replace(@"\bin\Debug", ""); // for Win
            #endregion

            var d = new DirectoryInfo(executionPath);
            var userConfig = (from FileInfo fi in d.GetFiles()
                where fi.FullName.EndsWith(userConfigFilename + ".conf") ||
                      fi.FullName.EndsWith(userConfigFilename + ".json") ||
                      fi.FullName.EndsWith(userConfigFilename + ".conf.json") ||
                      fi.FullName.EndsWith(userConfigFilename + ".json.conf")
                select fi).FirstOrDefault();

            if (userConfig != null)
            {
                User = ParseJson(File.ReadAllText(userConfig.FullName));
                WatchUserConfig(userConfig);
            }
            else
            {
                User = new NullExceptionPreventer();
            }
        }

        public static dynamic MergedConfig => Merger.Merge(User, Default);

        public static dynamic Global
        {
            get => _globalConfig ?? (_globalConfig = MergedConfig);
            set => _globalConfig = Merger.Merge(value, MergedConfig);
        }

        /// <summary>
        ///     Gets a ConfigObject that represents the current configuration. Since it is
        ///     a cloned copy, changes to the underlying configuration files that are done
        ///     after GetCurrentScope() is called, are not applied in the returned instance.
        /// </summary>
        public static ConfigObject GetCurrentScope()
        {
            if (Global is NullExceptionPreventer)
                return new ConfigObject();
            return Global.Clone();
        }

        public static event UserConfigFileChangedHandler OnUserConfigFileChanged;

        public static void WatchUserConfig(FileInfo info)
        {
            var lastRead = File.GetLastWriteTime(info.FullName);
            _userConfigWatcher =
                new FileSystemWatcher(info.Directory.FullName, info.Name) {NotifyFilter = NotifyFilters.LastWrite};
            _userConfigWatcher.Changed += delegate
            {
                var lastWriteTime = File.GetLastWriteTime(info.FullName);
                if (lastWriteTime.Subtract(lastRead).TotalMilliseconds > 100)
                {
                    Console.WriteLine("user configuration has changed, updating config information");
                    try
                    {
                        User = ParseJson(File.ReadAllText(info.FullName));
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(100); //Sleep shortly, and try again.
                        try
                        {
                            User = ParseJson(File.ReadAllText(info.FullName));
                        }
                        catch (Exception)
                        {
                            Console.WriteLine("updating user config failed.");
                            throw;
                        }
                    }


                    // invalidate the Global config, forcing a re-merge next time its accessed
                    _globalConfig = null;

                    // trigger our event
                    OnUserConfigFileChanged?.Invoke();
                }
                lastRead = lastWriteTime;
            };
            _userConfigWatcher.EnableRaisingEvents = true;
        }

        public static ConfigObject ApplyJsonFromFileInfo(FileInfo file, ConfigObject config = null)
        {
            var overlayJson = File.ReadAllText(file.FullName);
            dynamic overlayConfig = ParseJson(overlayJson);
            return Merger.Merge(overlayConfig, config);
        }

        public static ConfigObject ApplyJsonFromPath(string path, ConfigObject config = null)
        {
            return ApplyJsonFromFileInfo(new FileInfo(path), config);
        }

        public static ConfigObject ApplyJson(string json, ConfigObject config = null)
        {
            if (config == null)
                config = new ConfigObject();

            dynamic parsed = ParseJson(json);
            return Merger.Merge(parsed, config);
        }

        // seeks a folder for .conf files
        public static ConfigObject ApplyFromDirectory(string path, ConfigObject config = null, bool recursive = false)
        {
            if (!Directory.Exists(path))
                throw new Exception("no folder found in the given path");

            if (config == null)
                config = new ConfigObject();

            var info = new DirectoryInfo(path);
            if (recursive)
                foreach (var dir in info.GetDirectories())
                {
                    Console.WriteLine("reading in folder {0}", dir);
                    config = ApplyFromDirectoryInfo(dir, config, true);
                }

            // find all files
            var files = info.GetFiles();
            foreach (var file in files)
            {
                Console.WriteLine("reading in file {0}", file);
                config = ApplyJsonFromFileInfo(file, config);
            }
            return config;
        }

        public static ConfigObject ApplyFromDirectoryInfo(DirectoryInfo info, ConfigObject config = null,
            bool recursive = false)
        {
            return ApplyFromDirectory(info.FullName, config, recursive);
        }

        public static ConfigObject ParseJson(string json)
        {
            var lines = json.Split('\n');
            // remove lines that start with a dash # character 
            var filtered = from l in lines
                where !Regex.IsMatch(l, @"^\s*#(.*)")
                select l;

            var filteredJson = string.Join("\n", filtered);

            var parsed = JsonConvert.DeserializeObject<ExpandoObject>(filteredJson, new ExpandoObjectConverter());

            // transform the ExpandoObject to the format expected by ConfigObject
            parsed = JsonNetAdapter.Transform(parsed);

            // convert the ExpandoObject to ConfigObject before returning
            var result = ConfigObject.FromExpando(parsed);
            return result;
        }

        // overrides any default config specified in default.conf
        public static void SetDefaultConfig(dynamic config)
        {
            Default = config;

            // invalidate the Global config, forcing a re-merge next time its accessed
            _globalConfig = null;
        }

        public static void SetUserConfig(ConfigObject config)
        {
            User = config;
            // disable the watcher
            if (_userConfigWatcher != null)
            {
                _userConfigWatcher.EnableRaisingEvents = false;
                _userConfigWatcher.Dispose();
                _userConfigWatcher = null;
            }

            // invalidate the Global config, forcing a re-merge next time its accessed
            _globalConfig = null;
        }

        private static dynamic GetDefaultConfig(Assembly assembly)
        {
            var dconfJson = ScanForDefaultConfig(assembly);
            return dconfJson == null ? null : ParseJson(dconfJson);
        }

        private static string ScanForDefaultConfig(Assembly assembly)
        {
            if (assembly == null)
                assembly = Assembly.GetEntryAssembly();

            string[] res;
            try
            {
                // this might fail for the 'Anonymously Hosted DynamicMethods Assembly' created by an Reflect.Emit()
                res = assembly.GetManifestResourceNames();
            }
            catch
            {
                // for those assemblies, we don't provide a config
                return null;
            }
            var dconfResource = res
                .FirstOrDefault(r => r.EndsWith("default.conf", StringComparison.OrdinalIgnoreCase) ||
                    r.EndsWith("default.json", StringComparison.OrdinalIgnoreCase) ||
                    r.EndsWith("default.conf.json", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(dconfResource))
                return null;

            var stream = assembly.GetManifestResourceStream(dconfResource);
            var defaultJson = new StreamReader(stream).ReadToEnd();
            return defaultJson;
        }
    }
}