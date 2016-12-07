﻿using JetBrains.ReSharper.Plugins.Unity.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.Psi.ShaderLab;
using JetBrains.ReSharper.TestFramework;
using NUnit.Framework;

namespace JetBrains.ReSharper.Plugins.Unity.Tests.Psi.ShaderLab.Parsing
{
    [TestUnity]
    [TestFileExtension(ShaderLabProjectFileType.SHADER_EXTENSION)]
    public class ParserTests : ParserTestBase<ShaderLabLanguage>
    {
        protected override string RelativeTestDataPath => @"psi\shaderLab\parsing";

        [TestCase("First")]
        public void TestLexer(string name) => DoOneTest(name);
    }
}