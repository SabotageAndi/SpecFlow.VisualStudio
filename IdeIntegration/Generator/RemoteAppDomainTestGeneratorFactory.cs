using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Policy;
using System.Threading;
using TechTalk.SpecFlow.Generator;
using TechTalk.SpecFlow.Generator.Interfaces;
using TechTalk.SpecFlow.IdeIntegration.Tracing;
using TechTalk.SpecFlow.RemoteAppDomain;

namespace TechTalk.SpecFlow.IdeIntegration.Generator
{
    public class RemoteAppDomainTestGeneratorFactory : IRemoteAppDomainTestGeneratorFactory
    {
        private const int APPDOMAIN_CLEANUP_SECONDS = 600;
        private const string LogCategory = "RemoteAppDomainTestGeneratorFactory";
        private readonly Info _info = new Info();

        private readonly IIdeTracer _tracer;

        protected AppDomain AppDomain { get; private set; }
        private RemoteAppDomainResolver _remoteAppDomainResolver;
        private ITestGeneratorFactory _remoteTestGeneratorFactory;
        private UsageCounter _usageCounter;
        public Timer CleanupTimer;
        private readonly Type _remoteAppDomainResolverType = typeof(RemoteAppDomainResolver);

        public RemoteAppDomainTestGeneratorFactory(IIdeTracer tracer)
        {
            _tracer = tracer;


            AppDomainCleanupTime = TimeSpan.FromSeconds(APPDOMAIN_CLEANUP_SECONDS);
            CleanupTimer = new Timer(CleanupTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);


        }

        public TimeSpan AppDomainCleanupTime { get; set; }

        public bool IsRunning
        {
            get { return AppDomain != null; }
        }

        public void Setup(string newGeneratorFolder)
        {
            if (_info.GeneratorFolder == newGeneratorFolder)
                return;

            Cleanup();
            _info.GeneratorFolder = newGeneratorFolder;
        }

        public void EnsureInitialized()
        {
            if (!IsRunning)
                Initialize();
        }

        public Version GetGeneratorVersion()
        {
            EnsureInitialized();
            try
            {
                _usageCounter.Increase();
                return _remoteTestGeneratorFactory.GetGeneratorVersion();
            }
            finally
            {
                _usageCounter.Decrease();
            }
        }

        public ITestGenerator CreateGenerator(ProjectSettings projectSettings)
        {
            EnsureInitialized();
            _usageCounter.Increase();
            var remoteGenerator = _remoteTestGeneratorFactory.CreateGenerator(projectSettings);


            var disposeNotificationGenerator = new DisposeNotificationTestGenerator(remoteGenerator, _remoteAppDomainResolver);
            disposeNotificationGenerator.Disposed += () => _usageCounter.Decrease();
            return disposeNotificationGenerator;
        }

        public void Dispose()
        {
            Cleanup();
        }

        public void Cleanup()
        {
            if (!IsRunning)
                return;

            AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomainOnAssemblyResolve;
            _tracer.Trace("AssemblyResolve Event removed", LogCategory);
            _remoteAppDomainResolver.Dispose();

            _remoteTestGeneratorFactory = null;
            AppDomain.Unload(AppDomain);
            AppDomain = null;
            _tracer.Trace("AppDomain for generator disposed", LogCategory);
        }

        private void Initialize()
        {
            if (_info.GeneratorFolder == null)
                throw new InvalidOperationException(
                    "The RemoteAppDomainTestGeneratorFactory has to be configured with the Setup() method before initialization.");




            var appDomainSetup = new AppDomainSetup
            {
                ShadowCopyFiles = "true",
            };

            var appConfigFile = Path.Combine(_info.GeneratorFolder, "plugincompability.config");
            if (File.Exists(appConfigFile))
            {
                appDomainSetup.ConfigurationFile = PatchDevEnvAppConfig(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile, appConfigFile);
            }


            AppDomain = AppDomain.CreateDomain("AppDomainForTestGeneration", null, appDomainSetup);

            var testGeneratorFactoryTypeFullName = typeof(TestGeneratorFactory).FullName;
            Debug.Assert(testGeneratorFactoryTypeFullName != null);

            _tracer.Trace(string.Format("TestGeneratorFactory: {0}", testGeneratorFactoryTypeFullName), LogCategory);

            _tracer.Trace("AssemblyResolve Event added", LogCategory);
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;


            var remoteAppDomainAssembly = _remoteAppDomainResolverType.Assembly;
            _remoteAppDomainResolver = (RemoteAppDomainResolver)AppDomain.CreateInstanceFromAndUnwrap(remoteAppDomainAssembly.Location, _remoteAppDomainResolverType.FullName, true, BindingFlags.Default, null, null, null, null);
            _remoteAppDomainResolver.Init(_info.GeneratorFolder);

            var generatorFactoryObject = AppDomain.CreateInstanceAndUnwrap(_info.RemoteGeneratorAssemblyName, testGeneratorFactoryTypeFullName);
            _remoteTestGeneratorFactory = generatorFactoryObject as ITestGeneratorFactory;

            if (_remoteTestGeneratorFactory == null)
                throw new InvalidOperationException("Could not load test generator factory.");

            _usageCounter = new UsageCounter(LoseReferences);
            _tracer.Trace("AppDomain for generator created", LogCategory);
        }

        private string PatchDevEnvAppConfig(string devEnvAppConfigFile, string appConfigFile)
        {
            var devEnvAppConfigContent = File.ReadAllText(devEnvAppConfigFile);
            var pluginCompabilityConfig = File.ReadAllText(appConfigFile);

            var firstDependenAssembly = pluginCompabilityConfig.IndexOf("<dependentAssembly>");
            var endOfAssemblyBinding = pluginCompabilityConfig.LastIndexOf("</assemblyBinding>");
            var devenvEndOfAssemblyBinding = devEnvAppConfigContent.LastIndexOf("</assemblyBinding>");

            var assemblyRedirects = pluginCompabilityConfig.Substring(firstDependenAssembly, endOfAssemblyBinding - firstDependenAssembly);

            var patchedDevEnvConfig = devEnvAppConfigContent.Insert(devenvEndOfAssemblyBinding, assemblyRedirects);


            var patchedAppConfig = Path.ChangeExtension(Path.GetTempFileName(), "config");

            File.WriteAllText(patchedAppConfig, patchedDevEnvConfig);

            return patchedAppConfig;
        }

        private Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = args.Name.Split(new[] { ',' }, 2)[0];
            _tracer.Trace(string.Format("GeneratorAssemlbyResolveEvent: Name: {0}; ", args.Name), LogCategory);

            var assemblyAlreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName == args.Name).SingleOrDefault();
            if (assemblyAlreadyLoaded != null)
            {
                return assemblyAlreadyLoaded;
            }


            var extensionPath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), assemblyName + ".dll");
            if (File.Exists(extensionPath))
            {
                return Assembly.LoadFile(extensionPath);
            }

            return null;
        }

        private void LoseReferences()
        {
            CleanupTimer.Change(AppDomainCleanupTime, TimeSpan.FromMilliseconds(-1));
        }

        private void CleanupTimerElapsed(object state)
        {
            if (!_usageCounter.HasRefernece)
                Cleanup();
        }

        private class UsageCounter
        {
            private readonly Action cleanup;
            private int counter;

            public UsageCounter(Action cleanup)
            {
                this.cleanup = cleanup;
            }

            public bool HasRefernece
            {
                get { return counter > 0; }
            }

            public void Increase()
            {
                counter++;
            }

            public void Decrease()
            {
                if (--counter <= 0)
                    cleanup();
            }
        }

        public class DisposeNotificationTestGenerator : ITestGenerator
        {
            private readonly ITestGenerator _innerGenerator;
            public RemoteAppDomainResolver RemoteAppDomainResolver { get; private set; }

            public DisposeNotificationTestGenerator(ITestGenerator innerGenerator, RemoteAppDomainResolver remoteAppDomainResolver)
            {
                _innerGenerator = innerGenerator;
                RemoteAppDomainResolver = remoteAppDomainResolver;
            }

            public void Dispose()
            {
                _innerGenerator.Dispose();
                if (Disposed != null)
                    Disposed();
            }

            public TestGeneratorResult GenerateTestFile(FeatureFileInput featureFileInput, GenerationSettings settings)
            {
                return _innerGenerator.GenerateTestFile(featureFileInput, settings);
            }

            public Version DetectGeneratedTestVersion(FeatureFileInput featureFileInput)
            {
                return _innerGenerator.DetectGeneratedTestVersion(featureFileInput);
            }

            public string GetTestFullPath(FeatureFileInput featureFileInput)
            {
                return _innerGenerator.GetTestFullPath(featureFileInput);
            }

            public event Action Disposed;

            public override string ToString()
            {
                return _innerGenerator.ToString();
            }
        }
    }
}