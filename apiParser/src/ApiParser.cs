using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using JetBrains.Annotations;

namespace ApiParser
{
    public class ApiParser
    {
        // "Namespace:" is only used in 5.0
        private static readonly Regex NsRegex = new Regex(@"^((?<type>class|struct) in|Namespace:)\W*(?<namespace>\w+(?:\.\w+)*)$");
        private static readonly Regex SigRegex = new Regex(@"^(?:[\w.]+)?\.(\w+)(?:\((.*)\)|(.*))$");
        private static readonly Regex CoroutineRegex = new Regex(@"(?:can be|as) a co-routine", RegexOptions.IgnoreCase);

        private readonly UnityApi myApi;
        private readonly string myScriptReferenceRelativePath;

        public ApiParser(UnityApi api, string scriptReferenceRelativePath)
        {
            myApi = api;
            myScriptReferenceRelativePath = scriptReferenceRelativePath;
        }

        public event EventHandler<ProgressEventArgs> Progress;

        public void ExportTo(XmlTextWriter writer)
        {
            myApi.ExportTo(writer);
        }

        public void ParseFolder(string path, Version apiVersion)
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(path);

                var files = Directory.EnumerateFiles(myScriptReferenceRelativePath, @"*.html").ToArray();

                for (var i = 0; i < files.Length; ++i)
                {
                    ParseFile(files[i], apiVersion);
                    OnProgress(new ProgressEventArgs(i + 1, files.Length));
                }

            }
            finally
            {
                Directory.SetCurrentDirectory(currentDirectory);
            }

            Console.WriteLine();
        }

        [CanBeNull]
        private static ApiNode PickExample([NotNull] ApiNode details, [NotNull] string type)
        {
            var example = details.SelectOne($@"div.subsection/pre.codeExample{type}");
            return example == null || example.Text.StartsWith("no example available") ? null : example;
        }

        [CanBeNull]
        private static ApiNode PickExample([NotNull] ApiNode details)
        {
            // Favour C#, it's the most strongly typed
            return PickExample(details, "CS") ?? PickExample(details, "JS") ?? PickExample(details, "Raw");
        }

        private void OnProgress([NotNull] ProgressEventArgs e)
        {
            Progress?.Invoke(this, e);
        }

        private void ParseFile(string filename, Version apiVersion)
        {
            var document = ApiNode.Load(filename);
            var section = document?.SelectOne(@"//div.content/div.section");
            var header = section?.SelectOne(@"div.mb20.clear");
            var name = header?.SelectOne(@"h1.heading.inherit"); // Type or type member name
            var ns = header?.SelectOne(@"p");   // "class in {ns}"/"struct in {ns}"/"Namespace: {ns}"

            // Only interested in types at this point
            if (name == null || ns == null) return;

            // Only types that have messages
            var messages = section.Subsection("Messages").ToArray();
            if (messages.Length == 0) return;

            var match = NsRegex.Match(ns.Text);
            var clsType = match.Groups["type"].Value;
            var nsName = match.Groups["namespace"].Value;

            if (string.IsNullOrEmpty(clsType)) clsType = "class";
            if (string.IsNullOrEmpty(nsName)) return;

            var unityApiType = myApi.AddType(nsName, name.Text, clsType, filename, apiVersion);

            foreach (var message in messages)
            {
                var eventFunction = ParseMessage(message, apiVersion, nsName);
                unityApiType.MergeEventFunction(eventFunction, apiVersion);
            }
        }

        [CanBeNull]
        private UnityApiEventFunction ParseMessage(ApiNode message, Version apiVersion, string hintNamespace)
        {
            var link = message.SelectOne(@"td.lbl/a");
            var desc = message.SelectOne(@"td.desc");
            if (link == null || desc == null) return null;

            var detailsPath = link[@"href"];
            if (string.IsNullOrWhiteSpace(detailsPath)) return null;

            var path = Path.Combine(myScriptReferenceRelativePath, detailsPath);
            if (!File.Exists(path)) return null;

            var detailsDoc = ApiNode.Load(path);
            var details = detailsDoc?.SelectOne(@"//div.content/div.section");
            var signature = details?.SelectOne(@"div.mb20.clear/h1.heading.inherit");
            var staticNode = details?.SelectOne(@"div.subsection/p/code.varname[text()='static']");

            if (signature == null) return null;

            var isCoroutine = CoroutineRegex.IsMatch(details.Text);

            var messageName = link.Text;
            var returnType = ApiType.Void;
            string[] argumentNames = null;
            var isStaticFromExample = false;

            var example = PickExample(details);
            if (example != null)
            {
                var tuple = ParseDetailsFromExample(messageName, example, hintNamespace);
                returnType = tuple.Item1;
                argumentNames = tuple.Item2;
                isStaticFromExample = tuple.Item3;
            }

            var docPath = Path.Combine(myScriptReferenceRelativePath, detailsPath);
            var eventFunction = new UnityApiEventFunction(messageName, staticNode != null || isStaticFromExample, isCoroutine,
                returnType, apiVersion, desc.Text, docPath);

            ParseParameters(eventFunction, signature, details, hintNamespace, argumentNames);

            return eventFunction;
        }

        private static void ParseParameters(UnityApiEventFunction  eventFunction, ApiNode signature, ApiNode details, string owningMessageNamespace, string[] argumentNames)
        {
            // E.g. OnCollisionExit2D(Collision2D) - doesn't always include the argument name
            // Hopefully, we parsed the argument name from the example
            var argumentString = SigRegex.Replace(signature.Text, "$2$3");
            if (string.IsNullOrWhiteSpace(argumentString)) return;

            var argumentStrings = argumentString.Split(',')
                .Select(s => s.Trim())
                .ToArray();
            var total = argumentStrings.Length;
            var arguments = argumentStrings.Select((s, i) => new Argument(s, i, total, owningMessageNamespace)).ToArray();

            ResolveArguments(details, arguments, argumentNames);

            foreach (var argument in arguments)
                eventFunction.AddParameter(argument.Name, argument.Type, argument.Description);
        }

        private static void ResolveArguments([NotNull] ApiNode details, [NotNull] IReadOnlyList<Argument> arguments, string[] argumentNames)
        {
            if (argumentNames != null)
            {
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (!string.IsNullOrEmpty(argumentNames[i]))
                        arguments[i].Name = argumentNames[i];
                }
            }

            var parameters = details.Subsection("Parameters").ToArray();
            if (parameters.Any())
                ParseMessageParameters(arguments, parameters);
        }

        private static void ParseMessageParameters([NotNull] IEnumerable<Argument> arguments, [NotNull] IReadOnlyList<ApiNode> parameters)
        {
            var i = 0;
            foreach (var argument in arguments)
            {
                argument.Name = parameters[i].SelectOne(@"td.name.lbl")?.Text ?? argument.Name;
                argument.Description = parameters[i].SelectOne(@"td.desc")?.Text ?? argument.Description;
                ++i;
            }
        }

        // Gets return type and argument names from example
        private static Tuple<ApiType, string[], bool> ParseDetailsFromExample(string messageName, ApiNode example, string owningMessageNamespace)
        {
            var blankCleanup1 = new Regex(@"\s+");
            var blankCleanup2 = new Regex(@"\s*(\W)\s*");
            var arrayFixup = new Regex(@"(\[\])(\w)");

            var exampleText = example.Text;
            exampleText = blankCleanup1.Replace(exampleText, " ");
            exampleText = blankCleanup2.Replace(exampleText, "$1");
            exampleText = arrayFixup.Replace(exampleText, "$1 $2");

            var jsRegex = new Regex($@"(?:\W|^)(?<static>static\s+)?function {messageName}\((?<parameters>[^)]*)\)(?::(?<returnType>\w+\W*))?\{{");
            var m = jsRegex.Match(exampleText);
            if (m.Success)
            {
                var returnType = new ApiType(m.Groups["returnType"].Value, owningMessageNamespace);
                var parameters = m.Groups["parameters"].Value.Split(',');
                var isStatic = m.Groups["static"].Success;

                var arguments = new string[parameters.Length];
                for (var i = 0; i < parameters.Length; ++i)
                {
                    arguments[i] = parameters[i].Split(':')[0];
                }

                return Tuple.Create(returnType, arguments, isStatic);
            }

            var csRegex = new Regex($@"(?:\W|^)(?<static>static\s+)?(?<returnType>\w+\W*) {messageName}\((?<parameters>[^)]*)\)");
            m = csRegex.Match(exampleText);
            if (m.Success)
            {
                var nameRegex = new Regex(@"^.*?\W(\w+)$");

                var returnType = new ApiType(m.Groups["returnType"].Value, owningMessageNamespace);
                var parameters = m.Groups["parameters"].Value.Split(',');
                var isStatic = m.Groups["static"].Success;

                var arguments = new string[parameters.Length];
                for (var i = 0; i < parameters.Length; ++i)
                {
                    arguments[i] = nameRegex.Replace(parameters[i], "$1");
                }

                return Tuple.Create(returnType, arguments, isStatic);
            }

            return null;
        }
    }
}