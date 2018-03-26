using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TechTalk.SpecFlow.RemoteAppDomain
{
    [Serializable]
    public class RemoteAppDomainResolver : MarshalByRefObject, IDisposable
    {
        private readonly Info _info;
        private const string LogCategory = "RemoteAppDomainTestGeneratorFactory";

        public RemoteAppDomainResolver()
        {
            _info = new Info();
        }

        public List<string> LoadedAssemblies
        {
            get { return AppDomain.CurrentDomain.GetAssemblies().Select(a => $"{a.FullName}; {a.CodeBase}; {a.Location}").ToList(); }
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= AssemblyResolve;
        }

        public Type[] GetType(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetType(typeName) != null).Select(a => a.GetType(typeName)).ToArray();
        }

        public void DebuggingInAppDomain()
        {
            string typeName = "TechTalk.SpecFlow.Parser.SpecFlowFeature";
            var type = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetType(typeName) != null).Select(a => a.GetType(typeName)).SingleOrDefault();

            var properties = type.GetProperties();
        }


        private Assembly AssemblyResolve(object sender, ResolveEventArgs args)
        {
            Debug.WriteLine(String.Format("GeneratorAssemlbyResolveEvent: Name: {0}; ", args.Name), LogCategory);

            var assemblyAlreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName == args.Name).SingleOrDefault();
            if (assemblyAlreadyLoaded != null)
            {
                return assemblyAlreadyLoaded;
            }

            var assemblyName = args.Name.Split(new[] { ',' }, 2)[0];
           
            var extensionPath = Path.Combine(_info.GeneratorFolder, assemblyName + ".dll");
            if (File.Exists(extensionPath))
            {
                return Assembly.LoadFile(extensionPath);
            }

            return null;
        }

        public void Init(string infoGeneratorFolder)
        {
            _info.GeneratorFolder = infoGeneratorFolder;
            AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolve;

        }
    }
}