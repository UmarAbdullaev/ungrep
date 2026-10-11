using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityGraph.Core;
using UnityGraph.EditorLink;

namespace UnityGraph.Cli;

/// <summary>
/// `ug status`, `ug compile`, `ug run` and `ug bridge install|remove` (SPEC-editor-link, Commands):
/// the running editor through its bridge, or Editor.log when no bridge answers. Also the
/// editor-ops commands (SPEC-editor-ops, Commands): scenes, play mode, reads and edits, answered
/// with lines the bridge formats.
/// </summary>
internal static partial class EditorCommands
{
    public const string PackageName = "com.unitygraph.bridge";

    /// <summary>Where `ug bridge install --copy` puts the package: a vendored folder, so the graph skips it.</summary>
    public static readonly string CopyFolder = Path.Combine("Assets", "Plugins", "UnityGraphBridge");

    /// <summary>Replaces the check that the editor process in `bridge.json` is alive; tests only.</summary>
    internal static Func<int, bool>? ProcessCheck { get; set; }

    /// <summary>Replaces the fetch from docs.unity3d.com; tests only, they must not need the network.</summary>
    internal static Func<string, DocsFetchResult>? DocsFetchOverride { get; set; }

    /// <summary>Replaces the documentation cache folder; tests only.</summary>
    internal static DocsCache? DocsCacheOverride { get; set; }

    // Whether the last Op ended in a refusal or a failure: `ug batch` stops on it. Every ug run is
    // one thread, so a plain static does.
    private static bool _opFailed;

    // The connection `ug batch` shares between its steps; null outside a batch.
    private static BridgeClient? _session;

    private static (int, string) Failed(string text)
    {
        _opFailed = true;
        return (0, text);
    }

    // editorLog: the Editor.log to fall back on; null means Unity's default place. Tests only.
    public static IEnumerable<Command> Create(string? editorLog)
    {
        yield return Command("status", "The editor's state and last compile, through the bridge.", [], (root, _) => Status(root));

        var warnings = new Option<bool>("--warnings") { Description = "List warnings too, not only their count." };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for Unity. Default: 60.", DefaultValueFactory = _ => 60 };
        var all = new Option<bool>("--all") { Description = "List every error (and with --warnings every warning), not the first 20." };
        yield return Command("compile", "Refresh, wait for the script compile, and list its errors.", [warnings, timeout, all], (root, result) =>
            Compile(root, result.GetValue(warnings), TimeSpan.FromSeconds(Math.Max(1, result.GetValue(timeout))), editorLog ?? EditorLog.DefaultPath(), result.GetValue(all)));

        var copy = new Option<bool>("--copy") { Description = "Copy the package into " + CopyFolder.Replace('\\', '/') + "/ instead of printing how to add it." };
        var bridge = new Command("bridge", "Add the editor bridge to the project, or remove it.");
        bridge.Subcommands.Add(Command("install", "Print how to add the bridge package, or copy it with --copy.", [copy], (root, result) =>
            Install(root, result.GetValue(copy), FindPackage())));
        bridge.Subcommands.Add(Command("remove", "Delete the copy made by `ug bridge install --copy`.", [], (root, _) => Remove(root)));
        yield return bridge;

        foreach (Command command in OpsCommands().Concat(ConsoleCommands()).Concat(TestCommands()).Concat(LogCommands()))
        {
            yield return command;
        }

        yield return Batch();
    }

    public static (int ExitCode, string Text) Status(string root)
    {
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return (0, "Unity not reachable (" + connection.Problem + ")\n" + InstallHint(root));
        }

        using BridgeClient client = connection.Client;
        BridgeStatus status = connection.Status!;
        var text = new StringBuilder()
            .Append("Unity ").Append(status.Unity).Append("  ").Append(status.State).Append("  bridge ").Append(status.Bridge).Append('\n');
        if (status.Compile == 0)
        {
            return (0, text.Append("  last compile  none since the editor opened\n").ToString());
        }

        try
        {
            CompileResult last = client.LastCompile();
            text.Append("  last compile  ").Append(CompileText.Counts(last.Messages));
            if (last.Finished is { } finished)
            {
                text.Append("  ").Append(finished.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                if (last.Started is { } started)
                {
                    text.Append(" (").Append((finished - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s)");
                }
            }

            text.Append('\n');
        }
        catch (BridgeUnavailableException e)
        {
            text.Append("  last compile  unknown (").Append(e.Message).Append(")\n");
        }

        return (0, text.ToString());
    }

    public static (int ExitCode, string Text) Compile(string root, bool warnings, TimeSpan timeout, string? editorLog, bool all = false)
    {
        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return (0, CompileText.Fallback(root, connection.Problem!, editorLog, warnings, all));
        }

        using BridgeClient client = connection.Client;
        CompileOutcome outcome = client.Compile(timeout);
        return (0, outcome.Problem == null
            ? CompileText.Format(outcome, warnings, all)
            : CompileText.Fallback(root, outcome.Problem, editorLog, warnings, all));
    }

    public static (int ExitCode, string Text) Install(string root, bool copy, string? package)
    {
        if (package == null)
        {
            return (0, "the bridge package is missing from this unitygraph install (unity/" + PackageName + ")\n");
        }

        string? installed = Installed(root);
        if (!copy)
        {
            var text = new StringBuilder();
            if (installed != null)
            {
                text.Append("the bridge is already in this project (").Append(installed).Append(")\n");
            }

            return (0, text
                .Append("The bridge is a Unity package in the plugin: ").Append(Path.Combine(package, "package.json")).Append('\n')
                .Append("Add it one of two ways; both write into the project:\n")
                .Append("  1. In Unity: Window > Package Manager > + > Install package from disk..., and pick that file.\n")
                .Append("     Unity records it in Packages/manifest.json; ug never edits that file.\n")
                .Append("  2. ug bridge install --copy: copies it to ").Append(CopyFolder.Replace('\\', '/')).Append("/ (vendored, so not indexed).\n")
                .Append("Once Unity has compiled it, ug status shows the editor.\n")
                .ToString());
        }

        if (installed == "Packages/manifest.json")
        {
            return (0, "the bridge is already added through Package Manager (Packages/manifest.json); a copy would clash with it\n");
        }

        string destination = Path.Combine(root, CopyFolder);
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        int files = CopyPackage(package, destination);
        return (0, "copied the bridge (" + files.ToString(CultureInfo.InvariantCulture) + " files) to " + CopyFolder.Replace('\\', '/')
            + "/; Unity loads it when its window next has focus (click it once), then ug status shows the editor\n");
    }

    public static (int ExitCode, string Text) Remove(string root)
    {
        string destination = Path.Combine(root, CopyFolder);
        if (!Directory.Exists(destination))
        {
            return (0, Installed(root) == "Packages/manifest.json"
                ? "the bridge was added through Package Manager: remove it there (Window > Package Manager)\n"
                : "no bridge copy in " + CopyFolder.Replace('\\', '/') + "/\n");
        }

        Directory.Delete(destination, recursive: true);
        File.Delete(destination + ".meta");
        return (0, "removed " + CopyFolder.Replace('\\', '/') + "/\n");
    }

    /// <summary>How long an editor-ops request may take; opening and saving scenes get longer.</summary>
    public static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _sceneTimeout = TimeSpan.FromSeconds(60);

    // The Package Manager lists from its local cache; add and remove get --timeout (default 300 s).
    private static readonly TimeSpan _packageListTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// An editor-ops request: path and escaped query, the object addresses it edits (checked
    /// against the vendored folders), and how long to wait for the answer and for a play-mode change.
    /// </summary>
    public sealed record OpRequest(string PathAndQuery, IReadOnlyList<string?> Edits, TimeSpan Timeout, TimeSpan WaitTimeout)
    {
        /// <summary>A play-input request (SPEC-play-input): needs the module switch and bridge 0.4.0.</summary>
        public bool PlayInput { get; init; }

        /// <summary>A request that runs code (`ug run`, `ug eval`): needs bridge 0.5.0.</summary>
        public bool Code { get; init; }

        /// <summary>A camera request (`ug shot` viewpoints and sheets, `ug camera`): needs bridge 0.6.0.</summary>
        public bool Camera { get; init; }

        /// <summary>A UI Toolkit request (`ug nav`, `ug tree --ui`, a UXML shot): needs bridge 0.7.0.</summary>
        public bool UiToolkit { get; init; }

        /// <summary>A profiler request (`ug perf --record`, `ug perf profiler`, ...): needs bridge 0.8.0.</summary>
        public bool Profiler { get; init; }

        /// <summary>A C1 escape (`ug console --whole`, `ug inspect --field`, `--all`, ...): needs bridge 0.9.0.</summary>
        public bool Full { get; init; }

        /// <summary>The token budget for a docs page (`ug docs --budget`); null keeps the config's answer budget.</summary>
        public int? Budget { get; init; }

        /// <summary>The command that asks for this docs page again (`ug docs Manual/Prefabs`), named by a cut page.</summary>
        public string? Again { get; init; }

        /// <summary>The option that needs the newer bridge, named in the refusal: `ug shot --angles`.</summary>
        public string? Option { get; init; }

        /// <summary>Added to the message when Unity does not answer in time.</summary>
        public string? NoAnswer { get; init; }

        public OpRequest(string pathAndQuery, params string?[] edits)
            : this(pathAndQuery, edits, OpTimeout, TimeSpan.FromSeconds(60))
        {
        }
    }

    /// <summary>`/path?name=value&amp;...` with the values escaped; null values are left out, empty ones kept.</summary>
    public static string Query(string path, params (string Name, string? Value)[] parameters)
    {
        var text = new StringBuilder(path);
        char separator = '?';
        foreach ((string name, string? value) in parameters)
        {
            if (value != null)
            {
                text.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
                separator = '&';
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Sends one editor-ops request and prints its lines, its refusal, or why Unity did not answer;
    /// waits for play mode to change when the answer asks. <paramref name="command"/> is how the
    /// user typed it (`scene open`), for the messages.
    /// </summary>
    public static (int ExitCode, string Text) Op(string root, UnityGraphConfig config, string command, OpRequest request)
    {
        _opFailed = false;
        if (request.PlayInput && !config.IsEnabled("play-input"))
        {
            return Failed("play-input is off in " + UnityGraphConfig.RelativePath + "\n");
        }

        foreach (string? address in request.Edits)
        {
            string? path = address?.Trim().Split(':')[0];
            if (path != null && config.Vendored.Prefixes.FirstOrDefault(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            {
                return Failed(address!.Trim() + " is in the vendored folder " + prefix + ", which ug does not edit\n");
            }
        }

        if (_session != null)
        {
            return Ask(_session, config, command, request);
        }

        BridgeConnection connection = BridgeClient.Connect(root, ProcessCheck);
        if (connection.Client == null)
        {
            return Failed("Unity not reachable (" + connection.Problem + "); ug " + command + " needs the bridge\n" + InstallHint(root));
        }

        using BridgeClient client = connection.Client;
        return Ask(client, config, command, request);
    }

    // One request on a connected bridge; the connection is the caller's to dispose.
    private static (int ExitCode, string Text) Ask(BridgeClient client, UnityGraphConfig config, string command, OpRequest request)
    {
        if (!client.HasOps)
        {
            return Failed("bridge " + client.Info.Bridge + " is too old for ug " + command + ": run ug bridge install again\n");
        }

        if ((request.PlayInput && !client.HasPlayInput) || (request.Code && !client.HasCode) || (request.Camera && !client.HasCameras) || (request.UiToolkit && !client.HasUiToolkit)
            || (request.Profiler && !client.HasProfiler) || (request.Full && !client.HasFull))
        {
            return Failed("bridge " + client.Info.Bridge + " is too old for ug " + command + (request.Option == null ? "" : " " + request.Option) + ": run ug bridge install again\n");
        }

        var clock = Stopwatch.StartNew();
        OpAnswer answer;
        try
        {
            answer = client.Op(request.PathAndQuery, request.Timeout);
        }
        catch (BridgeUnavailableException e)
        {
            return Failed("no answer from Unity (" + e.Message + ")" + (request.NoAnswer == null ? "" : "; " + request.NoAnswer) + "\n");
        }

        if (answer.Local != null || answer.Online != null)
        {
            string page = DocsPages.Text(answer, config.DocsOnline, request.Budget ?? config.AnswerBudget, DocsCacheOverride ?? new DocsCache(DocsCache.DefaultFolder()), DocsFetchOverride ?? DocsFetcher.Fetch, out bool found, request.Again);
            _opFailed = !found;
            return (0, page);
        }

        if (answer.Error != null)
        {
            return Failed(answer.Error + "\n");
        }

        var lines = answer.Lines!.ToList();
        if (answer.Wait != null)
        {
            WaitOutcome waited = client.WaitFor(answer.Wait, request.WaitTimeout);
            if (!waited.Reached)
            {
                return Failed(waited.Problem + "\n");
            }

            if (answer.Wait == "playing" && lines.Count > 0)
            {
                lines[0] += " (entered in " + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)";
            }
        }

        return (0, string.Concat(lines.Select(line => line + "\n")));
    }

    /// <summary>`ug find --live`: the loaded scenes' objects by name part, tag, layer and component.</summary>
    public static (int ExitCode, string Text) LiveFind(string start, string? name, string? tag, string? layer, string? component, bool all = false) =>
        Start(start, (root, config) => Op(root, config, "find --live", new OpRequest(
            Query("/find", ("name", name), ("tag", tag), ("layer", layer), ("component", component), ("all", all ? "true" : null)))
        { Full = all, Option = all ? "--all" : null }));

    private static IEnumerable<Command> OpsCommands()
    {
        const string Address = "Object address: Player/Gun, Enemy#2, Main:Player, Assets/X.prefab:Child, or an asset path.";

        Command scene = Ops("scene", "scene", "Loaded scenes: active, modified, root count; and the play state.", [], _ => new OpRequest("/scene"));
        Argument<string> openPath = Required("path", "Scene asset path, like Assets/Scenes/Main.unity.");
        Option<bool> additive = Flag("--additive", "Keep the loaded scenes open.");
        scene.Subcommands.Add(Ops("open", "scene open", "Open a scene; refused while a loaded scene has unsaved changes (unless --additive).", [openPath, additive], r =>
            new OpRequest(Query("/scene/open", ("path", r.GetValue(openPath)), ("additive", True(r.GetValue(additive))))) { Timeout = _sceneTimeout }));
        Argument<string> newPath = Required("path", "Where to save the new scene, like Assets/Scenes/Level2.unity.");
        Option<bool> newAdditive = Flag("--additive", "Keep the loaded scenes open.");
        scene.Subcommands.Add(Ops("new", "scene new", "Create a scene, saved at the path.", [newPath, newAdditive], r =>
            new OpRequest(Query("/scene/new", ("path", r.GetValue(newPath)), ("additive", True(r.GetValue(newAdditive))))) { Timeout = _sceneTimeout }));
        Argument<string> closePath = Required("path", "Loaded scene path.");
        scene.Subcommands.Add(Ops("close", "scene close", "Close a loaded scene; refused when it has unsaved changes.", [closePath], r =>
            new OpRequest(Query("/scene/close", ("path", r.GetValue(closePath))))));
        Argument<string> activePath = Required("path", "Loaded scene path.");
        scene.Subcommands.Add(Ops("active", "scene active", "Make a loaded scene the active one.", [activePath], r =>
            new OpRequest(Query("/scene/active", ("path", r.GetValue(activePath))))));
        yield return scene;

        Argument<string?> savePath = Optional("path", "Only this loaded scene. Default: every modified scene.");
        yield return Ops("save", "save", "Save modified scenes and dirty assets: the only command that writes them.", [savePath], r =>
            new OpRequest(Query("/save", ("path", r.GetValue(savePath)))) { Timeout = _sceneTimeout });

        Option<int> playTimeout = Seconds();
        var playFor = new Option<int?>("--for") { Description = "Stay in play mode this many seconds (1 to 600), then stop and print the ug log digest." };
        var playStep = new Option<int?>("--step") { Description = "While paused, advance this many frames (1 to 600, default 1) and print what changed.", Arity = ArgumentArity.ZeroOrOne };
        Option<bool> playWhole = Flag("--whole", "With --for: the log's rows with their ids, whole messages and stacks (bridge 0.9.0).");
        yield return Command("play", "Enter play mode (or resume from pause) and wait until playing; --for plays a while, stops and prints the log; --step advances paused frames.", [playTimeout, playFor, playStep, playWhole], (root, config, r) =>
            Play(root, config, r.GetValue(playTimeout), r.GetValue(playFor), r.GetResult(playStep) == null ? null : r.GetValue(playStep) ?? 1, r.GetValue(playWhole)));
        Option<int> stopTimeout = Seconds();
        yield return Ops("stop", "stop", "Leave play mode; says how long it played and what it logged.", [stopTimeout], r =>
            new OpRequest("/stop") { WaitTimeout = TimeSpan.FromSeconds(Math.Max(1, r.GetValue(stopTimeout))) });
        yield return Ops("pause", "pause", "Pause play mode; ug play resumes.", [], _ => new OpRequest("/pause"));

        Argument<string?> treeObject = Optional("object", Address + " Default: the loaded scenes' roots.");
        var depth = new Option<int?>("--depth") { Description = "Levels to show. Default: 2 (10 with --ui)." };
        Option<string?> treeUi = Text("--ui", "Show the UI Toolkit visual tree under this object's UIDocument or an element (Menu, Menu/Pause): type, classes, text, screen place, state.");
        yield return Ops("tree", "tree", "The live hierarchy under an object: names, components, active state; --ui for a UI Toolkit visual tree.", [treeObject, depth, treeUi], r =>
            new OpRequest(Query("/tree", ("object", r.GetValue(treeObject)), ("depth", Number(r.GetValue(depth))), ("ui", r.GetValue(treeUi)))) { UiToolkit = r.GetValue(treeUi) != null, Option = r.GetValue(treeUi) != null ? "--ui" : null });

        Argument<string> inspected = Required("object", Address + " Or a UI Toolkit element: Menu/Play.");
        Option<string?> inspectComponent = Text("--component", "Show this component's fields: its type name, #n for the n-th.");
        Option<bool> inspectImporter = Flag("--importer", "Show an asset's import settings.");
        Option<string?> inspectField = Text("--field", "One field whole, as ug set names it (waves, waves[2].count): every element and level (bridge 0.9.0).");
        var inspectFrom = new Option<int?>("--from") { Description = "Start at this field, or with --field at this element (bridge 0.9.0)." };
        yield return Ops("inspect", "inspect", "An object's components, one component's serialized fields, or a UI Toolkit element's state and styles, live.", [inspected, inspectComponent, inspectImporter, inspectField, inspectFrom], r =>
            new OpRequest(Query("/inspect", ("object", r.GetValue(inspected)), ("component", r.GetValue(inspectComponent)), ("importer", True(r.GetValue(inspectImporter))),
                ("field", r.GetValue(inspectField)), ("from", Number(r.GetValue(inspectFrom) is { } from ? Math.Max(0, from) : null))))
            {
                Full = r.GetValue(inspectField) != null || r.GetValue(inspectFrom) != null,
                Option = r.GetValue(inspectField) != null ? "--field" : r.GetValue(inspectFrom) != null ? "--from" : null,
            });

        var objects = new Command("object", "Create, delete, rename, move, toggle and duplicate game objects (unsaved until ug save).");
        Argument<string?> createName = Optional("name", "Name of the new object.");
        Option<string?> createParent = Text("--parent", "Create under this object. Default: the active scene's root.");
        Option<string?> prefab = Text("--prefab", "Instantiate this prefab, kept linked.");
        Option<string?> primitive = Text("--primitive", "Cube, Sphere, Capsule, Cylinder, Plane or Quad.");
        Option<string?> createScene = Text("--scene", "At the root of this loaded scene (name without .unity).");
        objects.Subcommands.Add(Ops("create", "object create", "Create an empty object, a primitive or a prefab instance.", [createName, createParent, prefab, primitive, createScene], r =>
            new OpRequest(
                Query("/object/create", ("name", r.GetValue(createName)), ("parent", r.GetValue(createParent)), ("prefab", r.GetValue(prefab)), ("primitive", r.GetValue(primitive)), ("scene", r.GetValue(createScene))),
                r.GetValue(createParent))));
        Argument<string> deleted = Required("object", Address);
        objects.Subcommands.Add(Ops("delete", "object delete", "Delete an object and its children.", [deleted], r =>
            new OpRequest(Query("/object/delete", ("object", r.GetValue(deleted))), r.GetValue(deleted))));
        Argument<string> renamed = Required("object", Address);
        Argument<string> newName = Required("name", "The new name.");
        objects.Subcommands.Add(Ops("rename", "object rename", "Rename an object.", [renamed, newName], r =>
            new OpRequest(Query("/object/rename", ("object", r.GetValue(renamed)), ("name", r.GetValue(newName))), r.GetValue(renamed))));
        Argument<string> moved = Required("object", Address);
        Option<string?> moveParent = Text("--parent", "The new parent.");
        Option<bool> toRoot = Flag("--root", "Move to the root of its scene.");
        var index = new Option<int?>("--index") { Description = "Sibling index, 0 first." };
        Option<string?> moveScene = Text("--scene", "Move a root object to this loaded scene.");
        objects.Subcommands.Add(Ops("move", "object move", "Reparent an object (keeping its world position), reorder it, or move it to another scene.", [moved, moveParent, toRoot, index, moveScene], r =>
            new OpRequest(
                Query("/object/move", ("object", r.GetValue(moved)), ("parent", r.GetValue(toRoot) ? "" : r.GetValue(moveParent)), ("index", Number(r.GetValue(index))), ("scene", r.GetValue(moveScene))),
                r.GetValue(moved),
                r.GetValue(moveParent))));
        Argument<string> toggled = Required("object", Address);
        Argument<string> active = Required("value", "true or false.");
        objects.Subcommands.Add(Ops("active", "object active", "Activate or deactivate an object.", [toggled, active], r =>
            new OpRequest(Query("/object/active", ("object", r.GetValue(toggled)), ("value", r.GetValue(active))), r.GetValue(toggled))));
        Argument<string> duplicated = Required("object", Address);
        Option<string?> copyName = Text("--name", "Name of the copy. Default: the original's.");
        objects.Subcommands.Add(Ops("duplicate", "object duplicate", "Duplicate an object next to the original.", [duplicated, copyName], r =>
            new OpRequest(Query("/object/duplicate", ("object", r.GetValue(duplicated)), ("name", r.GetValue(copyName))), r.GetValue(duplicated))));
        yield return objects;

        var components = new Command("component", "Add or remove components (unsaved until ug save).");
        Argument<string> addTo = Required("object", Address);
        Argument<string> type = Required("type", "Component type: Rigidbody, PlayerController, or a full name.");
        components.Subcommands.Add(Ops("add", "component add", "Add a component.", [addTo, type], r =>
            new OpRequest(Query("/component/add", ("object", r.GetValue(addTo)), ("type", r.GetValue(type))), r.GetValue(addTo))));
        Argument<string> removeFrom = Required("object", Address);
        Argument<string> component = Required("component", "Component type, #n for the n-th of that type.");
        components.Subcommands.Add(Ops("remove", "component remove", "Remove a component; refused when another requires it.", [removeFrom, component], r =>
            new OpRequest(Query("/component/remove", ("object", r.GetValue(removeFrom)), ("component", r.GetValue(component))), r.GetValue(removeFrom))));
        yield return components;

        Argument<string> target = Required("object", Address + " Or a UI Toolkit element (live only, no undo): Menu/Play.");
        Argument<string> field = Required("field", "Serialized or display name: speed, m_LocalPosition, waves[2].count, list.size. An element: text, tooltip, name, enabled, display, visibility, class (+a -b ~c), value, style.<uss name>.");
        Argument<string> value = Required("value", "Number, true/false, text, enum name, 1,2,3, #ff8800, an object address, an asset path, or null.");
        Option<string?> setComponent = Text("--component", "The component with the field. Default: the object, then the one component that has it.");
        Option<bool> setImporter = Flag("--importer", "Set an asset's import setting, then reimport.");
        yield return Ops("set", "set", "Set one serialized field through Undo (unsaved until ug save).", [target, field, value, setComponent, setImporter], r =>
            new OpRequest(
                Query("/set", ("object", r.GetValue(target)), ("component", r.GetValue(setComponent)), ("field", r.GetValue(field)), ("value", r.GetValue(value)), ("importer", True(r.GetValue(setImporter)))),
                r.GetValue(target)));

        var events = new Command("event", "List, add and remove the persistent calls of a UnityEvent field, like Button.onClick (unsaved until ug save).");
        Argument<string> eventsOf = Required("object", Address);
        Argument<string?> listedEvent = Optional("event", "Only this event, like onClick. Default: every event that has calls.");
        Option<string?> listComponent = Text("--component", "The component with the event.");
        events.Subcommands.Add(Ops("list", "event list", "Show the calls of an object's events.", [eventsOf, listedEvent, listComponent], r =>
            new OpRequest(Query("/event/list", ("object", r.GetValue(eventsOf)), ("event", r.GetValue(listedEvent)), ("component", r.GetValue(listComponent))))));
        Argument<string> eventHolder = Required("object", Address);
        Argument<string> eventName = Required("event", "The event field: onClick, onValueChanged, ...");
        Argument<string> eventTarget = Required("target", "Whose method to call: Canvas:UiHandler (a component), or an object address when one component has the method.");
        Argument<string> eventMethod = Required("method", "A public void method, or a property to set (enabled, text).");
        Option<string?> eventComponent = Text("--component", "The component with the event. Default: the one that has it.");
        Option<string?> eventArg = Text("--arg", "The value passed to a method that takes one int, float, string, bool or object. Without it a method taking the event's own argument gets that.");
        events.Subcommands.Add(Ops("add", "event add", "Call a method when the event fires.", [eventHolder, eventName, eventTarget, eventMethod, eventComponent, eventArg], r =>
            new OpRequest(
                Query("/event/add", ("object", r.GetValue(eventHolder)), ("event", r.GetValue(eventName)), ("target", r.GetValue(eventTarget)), ("method", r.GetValue(eventMethod)), ("component", r.GetValue(eventComponent)), ("arg", r.GetValue(eventArg))),
                r.GetValue(eventHolder))));
        Argument<string> eventFrom = Required("object", Address);
        Argument<string> eventRemoved = Required("event", "The event field: onClick, ...");
        Argument<string?> eventIndex = Optional("index", "Which call (ug event list); optional when the event has one.");
        Option<string?> removeComponent = Text("--component", "The component with the event.");
        events.Subcommands.Add(Ops("remove", "event remove", "Remove one call from an event.", [eventFrom, eventRemoved, eventIndex, removeComponent], r =>
            new OpRequest(
                Query("/event/remove", ("object", r.GetValue(eventFrom)), ("event", r.GetValue(eventRemoved)), ("index", r.GetValue(eventIndex)), ("component", r.GetValue(removeComponent))),
                r.GetValue(eventFrom))));
        yield return events;

        var prefabs = new Command("prefab", "Save an object as a prefab, list, apply or revert an instance's overrides, unpack an instance (`ug object create --prefab` instantiates).");
        Argument<string> listed = Required("object", "Prefab instance in a loaded scene (its root).");
        prefabs.Subcommands.Add(Ops("overrides", "prefab overrides", "List every override on an instance with its value, read-only: what apply or revert would take (bridge 0.9.0).", [listed], r =>
            new OpRequest(Query("/prefab/overrides", ("object", r.GetValue(listed)))) { Full = true }));
        Argument<string> prefabSource = Required("object", "Scene object to save: " + Address);
        Argument<string> prefabPath = Required("path", "Where to save it, like Assets/Prefabs/Crate.prefab. Refused when it exists.");
        prefabs.Subcommands.Add(Ops("create", "prefab create", "Save a scene object as a prefab asset and keep it linked (writes the asset now; the scene stays unsaved).", [prefabSource, prefabPath], r =>
            new OpRequest(
                Query("/prefab/create", ("object", r.GetValue(prefabSource)), ("path", r.GetValue(prefabPath))),
                r.GetValue(prefabSource),
                r.GetValue(prefabPath))));
        Argument<string> applied = Required("object", "Prefab instance in a loaded scene (its root).");
        prefabs.Subcommands.Add(Ops("apply", "prefab apply", "Write an instance's overrides into its prefab asset (writes the asset now).", [applied], (r, config) =>
            new OpRequest(
                Query("/prefab/apply", ("object", r.GetValue(applied)), ("vendored", VendoredPrefixes(config))),
                r.GetValue(applied))));
        Argument<string> reverted = Required("object", "Prefab instance in a loaded scene (its root).");
        prefabs.Subcommands.Add(Ops("revert", "prefab revert", "Throw an instance's overrides away, back to its prefab's values.", [reverted], r =>
            new OpRequest(Query("/prefab/revert", ("object", r.GetValue(reverted))), r.GetValue(reverted))));
        Argument<string> unpacked = Required("object", "Prefab instance in a loaded scene (its root).");
        Option<bool> completely = Flag("--completely", "Unpack nested prefab instances too.");
        prefabs.Subcommands.Add(Ops("unpack", "prefab unpack", "Cut an instance loose from its prefab (nested instances stay linked without --completely).", [unpacked, completely], r =>
            new OpRequest(Query("/prefab/unpack", ("object", r.GetValue(unpacked)), ("completely", True(r.GetValue(completely)))), r.GetValue(unpacked))));
        yield return prefabs;

        var assets = new Command("asset", "Create, move, rename, duplicate, delete (to the trash) and reimport assets. Writes at once; Unity cannot undo these.");
        Argument<string> folderPath = Required("path", "Folder under Assets/, like Assets/Art/Icons; missing parents are created.");
        assets.Subcommands.Add(Ops("folder", "asset folder", "Create a folder (and any missing parents).", [folderPath], r =>
            new OpRequest(Query("/asset/folder", ("path", r.GetValue(folderPath))), r.GetValue(folderPath))));
        Argument<string> createPath = Required("path", "Where to create it, like Assets/Materials/Paint.mat or Assets/Data/Wave1.asset. Refused when it exists.");
        Argument<string?> createType = Optional("type", "Folder, Material, a ScriptableObject type name (PanelSettings); none for a .uxml, .uss or .tss path.");
        Option<string?> createShader = Text("--shader", "Shader for a Material, like Universal Render Pipeline/Lit. Default: the render pipeline's.");
        assets.Subcommands.Add(Ops("create", "asset create", "Create a folder, a material, a ScriptableObject asset, or a UXML, USS or TSS file with Unity's starting content.", [createPath, createType, createShader], r =>
            new OpRequest(
                Query("/asset/create", ("path", r.GetValue(createPath)), ("type", r.GetValue(createType)), ("shader", r.GetValue(createShader))),
                r.GetValue(createPath))));
        Argument<string> moveFrom = Required("from", "Asset or folder to move.");
        Argument<string> moveTo = Required("to", "The new path, with the same extension. Refused when it exists.");
        assets.Subcommands.Add(Ops("move", "asset move", "Move an asset or folder; its .meta and GUID go with it.", [moveFrom, moveTo], (r, config) =>
            new OpRequest(
                Query("/asset/move", ("from", r.GetValue(moveFrom)), ("to", r.GetValue(moveTo)), ("vendored", VendoredPrefixes(config))),
                r.GetValue(moveFrom),
                r.GetValue(moveTo))));
        Argument<string> renameFrom = Required("path", "Asset or folder to rename.");
        Argument<string> renameTo = Required("name", "The new name; a file keeps its extension.");
        assets.Subcommands.Add(Ops("rename", "asset rename", "Rename an asset or folder in place; its GUID is kept.", [renameFrom, renameTo], (r, config) =>
            new OpRequest(
                Query("/asset/rename", ("path", r.GetValue(renameFrom)), ("name", r.GetValue(renameTo)), ("vendored", VendoredPrefixes(config))),
                r.GetValue(renameFrom))));
        Argument<string> copyFrom = Required("path", "Asset or folder to copy; may be in Packages/.");
        Argument<string?> copyTo = Optional("to", "Where the copy goes, under Assets/. Default: next to the original, Unity's unique name.");
        assets.Subcommands.Add(Ops("duplicate", "asset duplicate", "Copy an asset or folder to a new path with a new GUID.", [copyFrom, copyTo], r =>
            new OpRequest(Query("/asset/duplicate", ("path", r.GetValue(copyFrom)), ("to", r.GetValue(copyTo))), r.GetValue(copyTo))));
        Argument<string> trashPath = Required("path", "Asset or folder to delete.");
        assets.Subcommands.Add(Ops("delete", "asset delete", "Move an asset or folder to the OS trash; refused for open scenes and Assets/ itself.", [trashPath], (r, config) =>
            new OpRequest(Query("/asset/delete", ("path", r.GetValue(trashPath)), ("vendored", VendoredPrefixes(config))), r.GetValue(trashPath))));
        Argument<string> reimportPath = Required("path", "Asset or folder to reimport.");
        assets.Subcommands.Add(Ops("reimport", "asset reimport", "Force Unity to import an asset (a folder: everything under it) again.", [reimportPath], (r, config) =>
            new OpRequest(Query("/asset/reimport", ("path", r.GetValue(reimportPath)), ("vendored", VendoredPrefixes(config))), r.GetValue(reimportPath))));
        yield return assets;

        yield return Ops("project", "project", "Unity version, render pipeline, tags, layers, sorting layers and build scenes.", [], _ => new OpRequest("/project"));

        var packages = new Command("package", "List, add and remove packages through Unity's Package Manager. Unity writes Packages/manifest.json; add may download.");
        packages.Subcommands.Add(Ops("list", "package list", "The manifest's direct dependencies with their resolved versions (from the local cache, no download).", [], _ =>
            new OpRequest("/package/list") { Timeout = _packageListTimeout }));
        Argument<string> packageId = Required("id", "Package name (com.unity.cinemachine), name@version, a git URL or file:path.");
        Option<int> addTimeout = PackageSeconds();
        packages.Subcommands.Add(Ops("add", "package add", "Add a package; Unity resolves it (a registry or git package downloads) and updates the manifest.", [packageId, addTimeout], r =>
            new OpRequest(Query("/package/add", ("id", r.GetValue(packageId)))) { Timeout = TimeSpan.FromSeconds(Math.Max(1, r.GetValue(addTimeout))) }));
        Argument<string> packageName = Required("name", "Package name, like com.unity.cinemachine.");
        Option<int> removeTimeout = PackageSeconds();
        packages.Subcommands.Add(Ops("remove", "package remove", "Remove a package from the manifest.", [packageName, removeTimeout], r =>
            new OpRequest(Query("/package/remove", ("name", r.GetValue(packageName)))) { Timeout = TimeSpan.FromSeconds(Math.Max(1, r.GetValue(removeTimeout))) }));
        yield return packages;

        yield return ShotCommand();
        yield return CameraCommand();

        Argument<string> apiType = Required("type", "Type or Type.member: Transform, Physics.Raycast, UnityEngine.Rigidbody.");
        Argument<string?> apiMember = Optional("member", "A member of the type: its signatures, overloads and defaults.");
        Option<bool> apiAll = Flag("--all", "Every member name, constructor, overload, enum value and ambiguous candidate, no budget (bridge 0.9.0).");
        yield return Ops("api", "api", "A Unity or .NET type from the loaded assemblies: members, or one member's signatures.", [apiType, apiMember, apiAll], r =>
        {
            bool all = r.GetValue(apiAll);
            return new OpRequest(Query("/api", ("type", r.GetValue(apiType)), ("member", r.GetValue(apiMember)), ("all", True(all)))) { Full = all, Option = all ? "--all" : null };
        });

        Argument<string> docsPage = Required("page", "Manual/Prefabs, ScriptReference/Physics.Raycast, or a package page like com.unity.cinemachine/manual/index.");
        var docsBudget = new Option<int?>("--budget") { Description = "Tokens of page text, 200 to 8000. Default: answerBudget in .claude/unitygraph.json." };
        Command docs = Ops("docs", "docs", "A Unity documentation page as trimmed text: local copy, then the cache, then docs.unity3d.com (docs.online in .claude/unitygraph.json).", [docsPage, docsBudget], r =>
            new OpRequest(Query("/api", ("page", r.GetValue(docsPage))))
            {
                Timeout = TimeSpan.FromSeconds(20),
                Budget = r.GetValue(docsBudget),
                Again = "ug docs " + r.GetValue(docsPage),
            });
        docs.Validators.Add(result =>
        {
            if (result.GetValue(docsBudget) is < UnityGraphConfig.MinAnswerBudget or > UnityGraphConfig.MaxAnswerBudget)
            {
                result.AddError("--budget must be 200 to 8000");
            }
        });
        yield return docs;

        yield return PerfCommand();

        yield return Ops("undo", "undo", "Undo the editor's last change, like Ctrl+Z.", [], _ => new OpRequest("/undo"));
        yield return Ops("redo", "redo", "Redo the last undone change.", [], _ => new OpRequest("/redo"));

        Argument<string?> selected = Optional("object", Address + " Default: list the selection.");
        Option<bool> clear = Flag("--clear", "Clear the selection.");
        Option<bool> selectAll = Flag("--all", "List every selected object, not the first 20 (bridge 0.9.0).");
        yield return Ops("select", "select", "Show, set or clear the editor's selection.", [selected, clear, selectAll], r =>
            new OpRequest(Query("/select", ("object", r.GetValue(selected)), ("clear", True(r.GetValue(clear))), ("all", True(r.GetValue(selectAll)))))
            {
                Full = r.GetValue(selectAll),
                Option = r.GetValue(selectAll) ? "--all" : null,
            });

        Argument<string?> runTarget = Optional("target", "Namespace.Type.Method or Type.Method: any static method of the project's code; or a menu path like Tools/Build. With --list: a filter.");
        var runArgs = new Argument<string[]>("args")
        {
            Description = "Arguments in ug set's grammar: 3, 1.5, true, Hard (enum), 0,1,0 (vector), #ff8800, Player/Gun or Assets/X.mat (object), [a, b] (list), {a: 1} (struct), null; name=value names one.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<bool> runList = Flag("--list", "List what can be run: types with static methods; a filter like Tools. or Spawn lists signatures.");
        Option<bool> runMenu = Flag("--menu", "With --list: menu items instead of methods.");
        var runTimeout = new Option<int>("--timeout") { Description = "Seconds to wait for a returned Task or IEnumerator. Default: 60.", DefaultValueFactory = _ => 60 };
        Option<bool> runAll = Flag("--all", "With --list: every line, not the first 40 (bridge 0.9.0).");
        Command run = Ops("run", "run", "Run a static method of the project's code with arguments, or a menu item, in the open editor; prints its result, logs and what it modified.", [runTarget, runArgs, runList, runMenu, runTimeout, runAll], r =>
            RunRequest(r.GetValue(runTarget), r.GetValue(runArgs) ?? [], r.GetValue(runList), r.GetValue(runMenu), r.GetValue(runTimeout), r.GetValue(runAll)));
        run.Validators.Add(result =>
        {
            if (result.GetValue(runAll) && !result.GetValue(runList))
            {
                result.AddError("--all goes with --list");
            }
        });
        yield return run;

        yield return EvalCommand();

        foreach (Command command in InputCommands())
        {
            yield return command;
        }

        static Argument<string> Required(string name, string description) => new(name) { Description = description };
        static Argument<string?> Optional(string name, string description) => new(name) { Description = description, Arity = ArgumentArity.ZeroOrOne };
        static Option<string?> Text(string name, string description) => new(name) { Description = description };
        static Option<bool> Flag(string name, string description) => new(name) { Description = description };
        static Option<int> Seconds() => new("--timeout") { Description = "Seconds to wait for play mode to change. Default: 60.", DefaultValueFactory = _ => 60 };
        static Option<int> PackageSeconds() => new("--timeout") { Description = "Seconds to wait for the Package Manager. Default: 300.", DefaultValueFactory = _ => 300 };
        static string? True(bool flag) => flag ? "true" : null;

        // The vendored folders joined with |, for requests whose target the bridge learns or checks itself.
        static string VendoredPrefixes(UnityGraphConfig config) => string.Join('|', config.Vendored.Prefixes);
        static string? Number(int? number) => number?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>`/run`: the target and each argument as `a0`, `a1`, ...; or the list request. Waits the bridge's deadline plus 5 s.</summary>
    public static OpRequest RunRequest(string? target, string[] args, bool list, bool menu, int timeout, bool all = false)
    {
        int seconds = Math.Max(1, timeout);
        var parameters = new List<(string, string?)>();
        if (list)
        {
            parameters.Add(("list", target ?? ""));
            parameters.Add(("menu", menu ? "true" : null));
            parameters.Add(("all", all ? "true" : null));
        }
        else
        {
            parameters.Add(("target", target ?? ""));
            parameters.Add(("n", args.Length.ToString(CultureInfo.InvariantCulture)));
            parameters.AddRange(args.Select((arg, i) => ("a" + i.ToString(CultureInfo.InvariantCulture), (string?)arg)));
            parameters.Add(("timeout", seconds.ToString(CultureInfo.InvariantCulture)));
        }

        return new OpRequest(Query("/run", [.. parameters]))
        {
            Timeout = TimeSpan.FromSeconds(seconds + 5),
            Code = true,
            Full = all,
            Option = all ? "--all" : null,
            NoAnswer = "the code may still be running in the editor, and nothing stops it from outside",
        };
    }

    // An editor-ops command: `command` is how the user types it, for the messages.
    private static Command Ops(string name, string command, string description, Symbol[] symbols, Func<ParseResult, OpRequest> request) =>
        Ops(name, command, description, symbols, (result, _) => request(result));

    private static Command Ops(string name, string command, string description, Symbol[] symbols, Func<ParseResult, UnityGraphConfig, OpRequest> request) =>
        Command(name, description, symbols, (root, config, result) => Op(root, config, command, request(result, config)));

    /// <summary>The package folder shipped with the plugin: `unity/com.unitygraph.bridge` above ug's own folder.</summary>
    public static string? FindPackage()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "unity", PackageName);
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    // Where the project has the bridge: its manifest, the --copy folder, or nowhere.
    private static string? Installed(string root)
    {
        string manifest = Path.Combine(root, "Packages", "manifest.json");
        if (File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"" + PackageName + "\"", StringComparison.Ordinal))
        {
            return "Packages/manifest.json";
        }

        return File.Exists(Path.Combine(root, CopyFolder, "package.json")) ? CopyFolder.Replace('\\', '/') + "/" : null;
    }

    private static string InstallHint(string root) =>
        Installed(root) == null && !File.Exists(BridgeClient.DiscoveryPath(root))
            ? "  the bridge is not in this project; `ug bridge install` shows how to add it\n"
            : "";

    // The package without its own tests: in Assets/ they would compile into the user's test assemblies.
    private static int CopyPackage(string package, string destination)
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(package, file);
            string top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (top is "Tests" or "Tests.meta")
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            count++;
        }

        return count;
    }

    private static Command Command(string name, string description, Symbol[] symbols, Func<string, ParseResult, (int, string)> run) =>
        Command(name, description, symbols, (root, _, result) => run(root, result));

    private static Command Command(string name, string description, Symbol[] symbols, Func<string, UnityGraphConfig, ParseResult, (int, string)> run)
    {
        var project = new Option<string>("--project") { Description = "Folder inside the Unity project. Default: current folder." };
        var command = new Command(name, description);
        foreach (Symbol symbol in symbols)
        {
            if (symbol is Argument argument)
            {
                command.Arguments.Add(argument);
            }
            else
            {
                command.Options.Add((Option)symbol);
            }
        }

        command.Options.Add(project);
        command.SetAction(result =>
        {
            (int exitCode, string text) = Start(result.GetValue(project) ?? Directory.GetCurrentDirectory(), (root, config) => run(root, config, result));
            result.InvocationConfiguration.Output.Write(text.ReplaceLineEndings());
            return exitCode;
        });
        return command;
    }

    private static (int, string) Start(string start, Func<string, UnityGraphConfig, (int, string)> run)
    {
        UnityProject? unity = UnityProject.Find(start);
        if (unity is null)
        {
            return (1, $"no Unity project at or above {Path.GetFullPath(start)}\n");
        }

        UnityGraphConfig config;
        try
        {
            config = UnityGraphConfig.Load(unity.Root);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            return (1, $"{UnityGraphConfig.RelativePath}: {e.Message}\n");
        }

        return config.IsEnabled("editor-link")
            ? run(unity.Root, config)
            : (0, "editor-link is off in " + UnityGraphConfig.RelativePath + "\n");
    }
}
