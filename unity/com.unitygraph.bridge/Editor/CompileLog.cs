using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Records each script compile: a counter of finished compiles, its start and end, and its
    /// messages. Kept in SessionState (a successful compile reloads the domain right after) and
    /// written to `Library/unitygraph/compile.json`.
    /// </summary>
    internal sealed class CompileLog
    {
        private const string CountKey = "unitygraph.compile.count";
        private const string AnswerKey = "unitygraph.compile.answer";
        private const string ErrorsKey = "unitygraph.compile.errors";
        private const string WarningsKey = "unitygraph.compile.warnings";

        private static readonly Regex _code = new Regex(@": (?:error|warning|info) (?<code>[A-Za-z]+\d+): (?<text>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        private readonly string _projectRoot;
        private readonly string _file;
        private readonly List<Message> _messages = new List<Message>();
        private DateTime _started;
        private int _count;
        private int _errors;
        private int _warnings;

        internal sealed class Message
        {
            public string Assembly;
            public string File;
            public int Line;
            public int Column;
            public string Severity;
            public string Code;
            public string Text;
        }

        public CompileLog(string projectRoot, string graphFolder)
        {
            _projectRoot = projectRoot;
            _file = Path.Combine(graphFolder, "compile.json");
            _count = SessionState.GetInt(CountKey, 0);
            _errors = SessionState.GetInt(ErrorsKey, 0);
            _warnings = SessionState.GetInt(WarningsKey, 0);
        }

        /// <summary>Finished compiles seen in this editor session.</summary>
        public int Count => _count;

        public int Errors => _errors;

        public int Warnings => _warnings;

        /// <summary>The `/compile` answer: the last compile, or an empty one when none was seen.</summary>
        public string Answer
        {
            get
            {
                string answer = SessionState.GetString(AnswerKey, "");
                return answer.Length > 0 ? answer : new Json().Object().Field("compile", 0L).Key("messages").Array().EndArray().EndObject().ToString();
            }
        }

        public void Attach()
        {
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssembly;
            CompilationPipeline.compilationFinished += OnFinished;
        }

        public void Detach()
        {
            CompilationPipeline.compilationStarted -= OnStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssembly;
            CompilationPipeline.compilationFinished -= OnFinished;
        }

        private void OnStarted(object context)
        {
            _started = DateTime.UtcNow;
            _messages.Clear();
        }

        private void OnAssembly(string assemblyPath, CompilerMessage[] messages)
        {
            string assembly = Path.GetFileNameWithoutExtension(assemblyPath);
            foreach (CompilerMessage message in messages)
            {
                if (message.type != CompilerMessageType.Info)
                {
                    _messages.Add(Convert(assembly, message.file, message.line, message.column, message.type == CompilerMessageType.Error, message.message, _projectRoot));
                }
            }
        }

        private void OnFinished(object context)
        {
            int count = Count + 1;
            DateTime finished = DateTime.UtcNow;
            string answer = Write(count, _started == default ? finished : _started, finished, _messages);
            _count = count;
            _errors = _messages.FindAll(m => m.Severity == "error").Count;
            _warnings = _messages.Count - _errors;
            SessionState.SetInt(CountKey, _count);
            SessionState.SetInt(ErrorsKey, _errors);
            SessionState.SetInt(WarningsKey, _warnings);
            SessionState.SetString(AnswerKey, answer);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file, answer);
            }
            catch (IOException)
            {
                // compile.json is a convenience for readers without the bridge; the answer stays in SessionState.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static Message Convert(string assembly, string file, int line, int column, bool error, string text, string projectRoot)
        {
            var message = new Message
            {
                Assembly = assembly,
                File = string.IsNullOrEmpty(file) ? null : StackFrames.Relative(file, projectRoot) ?? file.Replace('\\', '/'),
                Line = line,
                Column = column,
                Severity = error ? "error" : "warning",
                Text = text ?? "",
            };
            Match match = _code.Match(message.Text);
            if (match.Success)
            {
                message.Code = match.Groups["code"].Value;
                message.Text = match.Groups["text"].Value;
            }

            return message;
        }

        public static string Write(int count, DateTime started, DateTime finished, List<Message> messages)
        {
            var json = new Json().Object()
                .Field("compile", count)
                .Field("started", started.ToString("o", CultureInfo.InvariantCulture))
                .Field("finished", finished.ToString("o", CultureInfo.InvariantCulture))
                .Key("messages").Array();
            foreach (Message m in messages)
            {
                json.Object()
                    .Field("assembly", m.Assembly)
                    .Field("file", m.File)
                    .Field("line", m.Line)
                    .Field("column", m.Column)
                    .Field("severity", m.Severity)
                    .Field("code", m.Code)
                    .Field("text", m.Text)
                    .EndObject();
            }

            return json.EndArray().EndObject().ToString();
        }
    }
}
