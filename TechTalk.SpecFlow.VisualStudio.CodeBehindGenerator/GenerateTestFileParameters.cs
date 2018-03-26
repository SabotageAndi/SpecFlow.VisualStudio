﻿using CommandLine;

namespace TechTalk.SpecFlow.VisualStudio.CodeBehindGenerator
{
    [Verb("GenerateTestFile")]
    public class GenerateTestFileParameters : CommonParameters
    {
        [Option]
        public string FeatureFile { get; set; }

        [Option]
        public string ProjectSettingsFile { get; set; }
    }
}