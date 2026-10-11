using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge.Tests
{
    /// <summary>`/asset/*` and `/set` on textures, models, audio and materials, on the OpsFixture assets.</summary>
    public class AssetOpsTests : OpsFixture
    {
        private const string Written = "; not undoable)";
        private const string TexturePath = Folder + "/Tex.png";
        private const string ModelPath = Folder + "/Tri.obj";
        private const string AudioPath = Folder + "/Beep.wav";
        private const string NotInAssets = "an asset path must be a project path under Assets/, like Assets/Art/Hero.png: ";
        private const string Vendored = "Assets/Plugins/|" + Folder + "/Kit/";

        [Test]
        public void Folder_creates_nested_folders()
        {
            Assert.AreEqual(
                Lines("created folder " + Folder + "/A/B  (written " + Folder + "/A/B" + Written),
                Ask("/asset/folder", "path", Folder + "/A/B/"));
            Assert.IsTrue(AssetDatabase.IsValidFolder(Folder + "/A/B"));
            Assert.IsTrue(File.Exists(Folder + "/A.meta") && File.Exists(Folder + "/A/B.meta"));

            Assert.AreEqual(Err(Folder + "/A/B already exists: ug does not overwrite it"), Ask("/asset/folder", "path", Folder + "/A/B"));
            Assert.AreEqual(Err(MaterialPath + " is a file, not a folder"), Ask("/asset/folder", "path", MaterialPath + "/X"));
            Assert.AreEqual(Err("no path given"), Ask("/asset/folder"));
            Assert.AreEqual(Err(NotInAssets + "Library/X"), Ask("/asset/folder", "path", "Library/X"));
            Assert.AreEqual(Err(NotInAssets + "Assets/../X"), Ask("/asset/folder", "path", "Assets/../X"));
            Assert.AreEqual(Err(NotInAssets + "Assets\\X"), Ask("/asset/folder", "path", "Assets\\X"));
            Assert.AreEqual(Err("Packages/com.x/Y is in a package, which ug does not edit: copy it into Assets/ first"), Ask("/asset/folder", "path", "Packages/com.x/Y"));
            Assert.AreEqual(
                Err(Folder + "/Kit/New is in the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/folder", "path", Folder + "/Kit/New", "vendored", Vendored));
            Assert.IsFalse(AssetDatabase.IsValidFolder(Folder + "/Kit"));
        }

        [Test]
        public void Create_makes_materials()
        {
            Assert.AreEqual(
                Lines("created " + Folder + "/New.mat (Material, shader Unlit/Texture)  (written " + Folder + "/New.mat" + Written),
                Ask("/asset/create", "path", Folder + "/New.mat", "type", "material", "shader", "Unlit/Texture"));
            Assert.AreEqual("Unlit/Texture", AssetDatabase.LoadAssetAtPath<Material>(Folder + "/New.mat").shader.name);

            StringAssert.StartsWith("{\"lines\":[\"created " + Folder + "/Plain.mat (Material, shader ", Ask("/asset/create", "path", Folder + "/Plain.mat", "type", "Material"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Plain.mat").shader);

            StringAssert.StartsWith("{\"error\":\"no shader Unlit/Textur (near: Unlit/Texture", Ask("/asset/create", "path", Folder + "/Bad.mat", "type", "Material", "shader", "Unlit/Textur"));
            Assert.AreEqual(Err("a material path must end in .mat: " + Folder + "/Bad.asset"), Ask("/asset/create", "path", Folder + "/Bad.asset", "type", "Material"));
            Assert.AreEqual(Err(MaterialPath + " already exists: ug does not overwrite it"), Ask("/asset/create", "path", MaterialPath, "type", "Material"));
            Assert.AreEqual(
                Err("the folder " + Folder + "/Nope does not exist: ug asset folder " + Folder + "/Nope makes it"),
                Ask("/asset/create", "path", Folder + "/Nope/X.mat", "type", "Material"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Bad.mat"));
        }

        [Test]
        public void Create_makes_scriptable_objects_and_folders()
        {
            Assert.AreEqual(
                Lines("created " + Folder + "/Wave1.asset (Ungrep.Bridge.Tests.UgData)  (written " + Folder + "/Wave1.asset" + Written),
                Ask("/asset/create", "path", Folder + "/Wave1.asset", "type", "UgData"));
            var data = AssetDatabase.LoadAssetAtPath<UgData>(Folder + "/Wave1.asset");
            Assert.AreEqual(3, data.count);
            Assert.IsNotNull(Ask("/asset/create", "path", Folder + "/Wave2.asset", "type", "ungrep.bridge.tests.ugdata"));

            Assert.AreEqual(
                Lines("created folder " + Folder + "/Made  (written " + Folder + "/Made" + Written),
                Ask("/asset/create", "path", Folder + "/Made", "type", "Folder"));
            StringAssert.StartsWith("{\"error\":\"no Material, Folder or ScriptableObject type UgDat (near: UgData", Ask("/asset/create", "path", Folder + "/X.asset", "type", "UgDat"));
            StringAssert.StartsWith("{\"error\":\"no Material, Folder or ScriptableObject type SceneView", Ask("/asset/create", "path", Folder + "/X.asset", "type", "SceneView"));
            Assert.AreEqual(Err("a ScriptableObject path must end in .asset: " + Folder + "/X.mat"), Ask("/asset/create", "path", Folder + "/X.mat", "type", "UgData"));
            Assert.AreEqual(Err("no type given (Folder, Material, a ScriptableObject type, or a .uxml, .uss or .tss path)"), Ask("/asset/create", "path", Folder + "/X.asset"));
            Assert.AreEqual(Err(NotInAssets + "ProjectSettings/X.asset"), Ask("/asset/create", "path", "ProjectSettings/X.asset", "type", "UgData"));
            Assert.AreEqual(
                Err("Packages/com.x/X.asset is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/asset/create", "path", "Packages/com.x/X.asset", "type", "UgData"));
        }

        [Test]
        public void Create_writes_ui_toolkit_files_with_unitys_starting_content()
        {
            string root = Path.GetDirectoryName(Application.dataPath) + "/";
            Assert.AreEqual(
                Lines("created " + Folder + "/Menu.uxml (UXML, empty)  (written " + Folder + "/Menu.uxml" + Written),
                Ask("/asset/create", "path", Folder + "/Menu.uxml"));
            Assert.AreEqual(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\" editor-extension-mode=\"False\">\n</ui:UXML>\n",
                File.ReadAllText(root + Folder + "/Menu.uxml"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(Folder + "/Menu.uxml"));

            Assert.AreEqual(Lines("created " + Folder + "/Menu.uss (USS)  (written " + Folder + "/Menu.uss" + Written), Ask("/asset/create", "path", Folder + "/Menu.uss", "type", "uss"));
            Assert.AreEqual("VisualElement {}\n", File.ReadAllText(root + Folder + "/Menu.uss"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.StyleSheet>(Folder + "/Menu.uss"));

            Assert.AreEqual(
                Lines("created " + Folder + "/Game.tss (TSS, imports Unity's default theme)  (written " + Folder + "/Game.tss" + Written),
                Ask("/asset/create", "path", Folder + "/Game.tss"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(Folder + "/Game.tss"));

            Assert.AreEqual(Err(Folder + "/Menu.uxml already exists: ug does not overwrite it"), Ask("/asset/create", "path", Folder + "/Menu.uxml"));
            Assert.AreEqual(Err("a USS path must end in .uss: " + Folder + "/X.uxml"), Ask("/asset/create", "path", Folder + "/X.uxml", "type", "USS"));
            Assert.AreEqual(Err("the folder " + Folder + "/Nope does not exist: ug asset folder " + Folder + "/Nope makes it"), Ask("/asset/create", "path", Folder + "/Nope/X.uss"));

            // The project has a theme now (Game.tss), so Unity gives the PanelSettings one and writes none of its own.
            bool themes = AssetDatabase.IsValidFolder("Assets/UI Toolkit");
            try
            {
                Assert.AreEqual(
                    Lines("created " + Folder + "/Panel.asset (UnityEngine.UIElements.PanelSettings; theme " + Folder + "/Game.tss)  (written " + Folder + "/Panel.asset" + Written),
                    Ask("/asset/create", "path", Folder + "/Panel.asset", "type", "PanelSettings"));
            }
            finally
            {
                if (!themes)
                {
                    AssetDatabase.DeleteAsset("Assets/UI Toolkit");
                }
            }
        }

        [Test]
        public void Move_keeps_the_guid_and_the_meta_file()
        {
            Ask("/asset/folder", "path", Folder + "/Sub");
            string guid = AssetDatabase.AssetPathToGUID(MaterialPath);
            string to = Folder + "/Sub/Moved.mat";

            Assert.AreEqual(
                Lines("moved " + MaterialPath + " -> " + to + "  (written " + to + "; guid " + guid + " kept" + Written),
                Ask("/asset/move", "from", MaterialPath, "to", to));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(to));
            Assert.IsTrue(File.Exists(to) && File.Exists(to + ".meta"));
            Assert.IsFalse(File.Exists(MaterialPath) || File.Exists(MaterialPath + ".meta"));

            string folderGuid = AssetDatabase.AssetPathToGUID(Folder + "/Sub");
            Assert.AreEqual(
                Lines("moved " + Folder + "/Sub -> " + Folder + "/Moved  (written " + Folder + "/Moved; guid " + folderGuid + " kept" + Written),
                Ask("/asset/move", "from", Folder + "/Sub/", "to", Folder + "/Moved"));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(Folder + "/Moved/Moved.mat"));
        }

        [Test]
        public void Move_refuses_bad_sources_and_targets()
        {
            Assert.AreEqual(Err(PrefabPath + " already exists: ug does not overwrite it"), Ask("/asset/move", "from", MaterialPath, "to", PrefabPath));
            Assert.AreEqual(Err("not found: " + Folder + "/Nope.mat"), Ask("/asset/move", "from", Folder + "/Nope.mat", "to", Folder + "/Y.mat"));
            Assert.AreEqual(Err("cannot move " + Folder + " into itself"), Ask("/asset/move", "from", Folder, "to", Folder + "/In"));
            Assert.AreEqual(Err("the extension must stay .mat: ug does not convert assets (" + Folder + "/Paint.txt)"), Ask("/asset/move", "from", MaterialPath, "to", Folder + "/Paint.txt"));
            Assert.AreEqual(
                Err("the folder " + Folder + "/Nope does not exist: ug asset folder " + Folder + "/Nope makes it"),
                Ask("/asset/move", "from", MaterialPath, "to", Folder + "/Nope/Paint.mat"));
            Assert.AreEqual(Err("Assets/ itself cannot be moved"), Ask("/asset/move", "from", "Assets", "to", "Assets/X"));
            Assert.AreEqual(Err(NotInAssets + "Library/Paint.mat"), Ask("/asset/move", "from", MaterialPath, "to", "Library/Paint.mat"));
            Assert.AreEqual(Err(NotInAssets + "ProjectSettings/TagManager.asset"), Ask("/asset/move", "from", "ProjectSettings/TagManager.asset", "to", Folder + "/T.asset"));
            Assert.AreEqual(Err("no path given"), Ask("/asset/move", "to", Folder + "/Y.mat"));
            Assert.AreEqual(Err("no destination given"), Ask("/asset/move", "from", MaterialPath));
            Assert.AreEqual(
                Err("Packages/com.x/Y.mat is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/asset/move", "from", MaterialPath, "to", "Packages/com.x/Y.mat"));
            Assert.IsTrue(File.Exists(MaterialPath));
        }

        [Test]
        public void Move_and_delete_refuse_vendored_folders_and_folders_holding_them()
        {
            Ask("/asset/folder", "path", Folder + "/Kit");
            Ask("/asset/create", "path", Folder + "/Kit/Thing.mat", "type", "Material");

            Assert.AreEqual(
                Err(Folder + "/Kit/Thing.mat is in the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/move", "from", Folder + "/Kit/Thing.mat", "to", Folder + "/Out.mat", "vendored", Vendored));
            Assert.AreEqual(
                Err(Folder + "/Kit is in the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/delete", "path", Folder + "/Kit", "vendored", Vendored));
            Assert.AreEqual(
                Err(Folder + " holds the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/move", "from", Folder, "to", "Assets/_UgOpsMoved", "vendored", Vendored));
            Assert.AreEqual(
                Err(Folder + "/Kit/Thing.mat is in the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/rename", "path", Folder + "/Kit/Thing.mat", "name", "Other", "vendored", Vendored));
            Assert.AreEqual(
                Err(Folder + "/Kit/Thing.mat is in the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/reimport", "path", Folder + "/Kit/Thing.mat", "vendored", Vendored));
            Assert.AreEqual(
                Err(Folder + "/Kit/Copy.mat is in the vendored folder " + Folder + "/Kit/, which ug does not edit"),
                Ask("/asset/duplicate", "path", MaterialPath, "to", Folder + "/Kit/Copy.mat", "vendored", Vendored));
            Assert.IsTrue(File.Exists(Folder + "/Kit/Thing.mat"));
        }

        [Test]
        public void Rename_keeps_the_extension_and_the_guid()
        {
            string guid = AssetDatabase.AssetPathToGUID(MaterialPath);
            Assert.AreEqual(
                Lines("renamed " + MaterialPath + " -> " + Folder + "/Ink.mat  (written " + Folder + "/Ink.mat; guid " + guid + " kept" + Written),
                Ask("/asset/rename", "path", MaterialPath, "name", "Ink"));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(Folder + "/Ink.mat"));

            StringAssert.StartsWith("{\"lines\":[\"renamed " + Folder + "/Ink.mat -> " + Folder + "/Dye.mat", Ask("/asset/rename", "path", Folder + "/Ink.mat", "name", "Dye.mat"));
            Ask("/asset/folder", "path", Folder + "/Old");
            StringAssert.StartsWith("{\"lines\":[\"renamed " + Folder + "/Old -> " + Folder + "/New ", Ask("/asset/rename", "path", Folder + "/Old", "name", "New"));

            Ask("/asset/create", "path", Folder + "/Other.mat", "type", "Material");
            Assert.AreEqual(Err(Folder + "/Other.mat already exists: ug does not overwrite it"), Ask("/asset/rename", "path", Folder + "/Dye.mat", "name", "Other"));
            Assert.AreEqual(Err(Folder + "/Other.unity already exists: ug does not overwrite it"), Ask("/asset/rename", "path", MainPath, "name", "Other"));
            Assert.AreEqual(Err("a new name has no folder: ug asset move " + Folder + "/Dye.mat <path> moves it"), Ask("/asset/rename", "path", Folder + "/Dye.mat", "name", "A/B"));
            Assert.AreEqual(Err("no new name given"), Ask("/asset/rename", "path", Folder + "/Dye.mat"));
            Assert.AreEqual(Err("not found: " + Folder + "/Nope.mat"), Ask("/asset/rename", "path", Folder + "/Nope.mat", "name", "X"));
        }

        [Test]
        public void Duplicate_copies_with_a_new_guid()
        {
            string guid = AssetDatabase.AssetPathToGUID(MaterialPath);
            string to = Folder + "/Copy.mat";
            string answer = Ask("/asset/duplicate", "path", MaterialPath, "to", to);
            string copyGuid = AssetDatabase.AssetPathToGUID(to);
            Assert.AreEqual(Lines("duplicated " + MaterialPath + " -> " + to + "  (written " + to + "; new guid " + copyGuid + Written), answer);
            Assert.AreNotEqual(guid, copyGuid);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(MaterialPath), "the original keeps its guid");

            StringAssert.StartsWith("{\"lines\":[\"duplicated " + MaterialPath + " -> " + Folder + "/Paint 1.mat", Ask("/asset/duplicate", "path", MaterialPath));
            Ask("/asset/folder", "path", Folder + "/Dir");
            Assert.AreEqual(Err("cannot copy " + Folder + "/Dir into itself"), Ask("/asset/duplicate", "path", Folder + "/Dir", "to", Folder + "/Dir/In"));

            Assert.AreEqual(Err(to + " already exists: ug does not overwrite it"), Ask("/asset/duplicate", "path", MaterialPath, "to", to));
            Assert.AreEqual(Err("not found: " + Folder + "/Nope.mat"), Ask("/asset/duplicate", "path", Folder + "/Nope.mat", "to", Folder + "/Y.mat"));
            Assert.AreEqual(Err("the extension must stay .mat: ug does not convert assets (" + Folder + "/Y.txt)"), Ask("/asset/duplicate", "path", MaterialPath, "to", Folder + "/Y.txt"));
            Assert.AreEqual(Err("Assets/ itself cannot be duplicated"), Ask("/asset/duplicate", "path", "Assets", "to", "Assets/Y"));
            Assert.AreEqual(Err("no path given"), Ask("/asset/duplicate"));
            Assert.AreEqual(Err(NotInAssets + "Library/Y.mat"), Ask("/asset/duplicate", "path", MaterialPath, "to", "Library/Y.mat"));
            Assert.AreEqual(
                Err("Packages/com.x/Y.mat is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/asset/duplicate", "path", MaterialPath, "to", "Packages/com.x/Y.mat"));
        }

        [Test]
        public void Duplicate_copies_a_package_asset_into_Assets()
        {
            const string source = "Packages/com.ungrep.bridge/package.json";
            Assert.AreEqual(
                Err(source + " is in a package: give a destination under Assets/"),
                Ask("/asset/duplicate", "path", source));

            string to = Folder + "/bridge.json";
            StringAssert.StartsWith("{\"lines\":[\"duplicated " + source + " -> " + to, Ask("/asset/duplicate", "path", source, "to", to));
            Assert.IsTrue(File.Exists(to));
        }

        [Test]
        public void Delete_moves_to_the_trash_and_removes_the_meta()
        {
            Assert.AreEqual(
                Lines("deleted " + MaterialPath + "  (moved to the OS trash with its .meta; not undoable)"),
                Ask("/asset/delete", "path", MaterialPath));
            Assert.IsFalse(File.Exists(MaterialPath) || File.Exists(MaterialPath + ".meta"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(MaterialPath));
            Assert.AreEqual(Err("not found: " + MaterialPath), Ask("/asset/delete", "path", MaterialPath));
        }

        [Test]
        public void Delete_refuses_Assets_open_scenes_and_outside_paths()
        {
            Assert.AreEqual(Err("Assets/ itself cannot be deleted"), Ask("/asset/delete", "path", "Assets"));
            Assert.AreEqual(Err("Assets/ itself cannot be deleted"), Ask("/asset/delete", "path", "Assets/"));
            Assert.AreEqual(Err(MainPath + " is an open scene: ug scene close " + MainPath + " first"), Ask("/asset/delete", "path", MainPath));
            Assert.AreEqual(Err(Folder + " holds the open scene " + MainPath + ": ug scene close " + MainPath + " first"), Ask("/asset/delete", "path", Folder));
            Assert.AreEqual(Err(NotInAssets + "ProjectSettings"), Ask("/asset/delete", "path", "ProjectSettings"));
            Assert.AreEqual(Err(NotInAssets + "Library/x"), Ask("/asset/delete", "path", "Library/x"));
            Assert.AreEqual(Err("Packages/com.ungrep.bridge is in a package, which ug does not edit: copy it into Assets/ first"), Ask("/asset/delete", "path", "Packages/com.ungrep.bridge"));
            Assert.AreEqual(Err("no path given"), Ask("/asset/delete"));
            Assert.IsTrue(File.Exists(MainPath) && File.Exists(MaterialPath));
        }

        [Test]
        public void Reimport_forces_an_import_of_an_asset_or_folder()
        {
            WriteTexture();
            StringAssert.StartsWith("{\"lines\":[\"reimported " + TexturePath + "  (", Ask("/asset/reimport", "path", TexturePath));
            StringAssert.StartsWith("{\"lines\":[\"reimported " + Folder + " and everything under it  (", Ask("/asset/reimport", "path", Folder));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath));

            Assert.AreEqual(Err("not found: " + Folder + "/Nope.png"), Ask("/asset/reimport", "path", Folder + "/Nope.png"));
            Assert.AreEqual(Err("Assets/ itself cannot be reimported"), Ask("/asset/reimport", "path", "Assets"));
            Assert.AreEqual(Err(NotInAssets + "Library/x"), Ask("/asset/reimport", "path", "Library/x"));
        }

        [Test]
        public void Asset_edits_are_not_undoable()
        {
            Undo.ClearAll();
            Ask("/asset/folder", "path", Folder + "/NoUndo");
            Ask("/asset/move", "from", MaterialPath, "to", Folder + "/NoUndo/Paint.mat");

            Assert.AreEqual(Lines("nothing to undo"), Ask("/undo"));
            Assert.IsTrue(File.Exists(Folder + "/NoUndo/Paint.mat"));
        }

        [Test]
        public void Set_texture_importer_settings()
        {
            WriteTexture();
            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            Assert.IsFalse(importer.isReadable);

            string answer = Ask("/set", "object", TexturePath, "importer", "true", "field", "isReadable", "value", "true");
            TestContext.WriteLine(answer);
            Assert.AreEqual(Lines(TexturePath + "  TextureImporter.isReadable  0 -> 1  (reimported; .meta written)"), answer);
            Assert.IsTrue(((TextureImporter)AssetImporter.GetAtPath(TexturePath)).isReadable);
            StringAssert.Contains("isReadable: 1", File.ReadAllText(TexturePath + ".meta"));

            StringAssert.StartsWith("{\"error\":\"no field nothing on ", Ask("/set", "object", TexturePath, "importer", "true", "field", "nothing", "value", "1"));
            Assert.AreEqual(Err(TexturePath + " isReadable: not a whole number: maybe"), Ask("/set", "object", TexturePath, "importer", "true", "field", "isReadable", "value", "maybe"));
            Assert.AreEqual(Err("not found: " + Folder + "/Nope.png"), Ask("/set", "object", Folder + "/Nope.png", "importer", "true", "field", "isReadable", "value", "true"));
            Assert.AreEqual(
                Err("Packages/com.x/Hero.png is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/set", "object", "Packages/com.x/Hero.png", "importer", "true", "field", "isReadable", "value", "true"));
        }

        [Test]
        public void Set_model_importer_settings()
        {
            File.WriteAllText(ModelPath, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            AssetDatabase.ImportAsset(ModelPath);
            Assert.IsInstanceOf<ModelImporter>(AssetImporter.GetAtPath(ModelPath));

            string answer = Ask("/set", "object", ModelPath, "importer", "true", "field", "globalScale", "value", "2.5");
            TestContext.WriteLine(answer);
            StringAssert.StartsWith("{\"lines\":[\"" + ModelPath + "  ModelImporter.globalScale  1 -> 2.5", answer);
            Assert.AreEqual(2.5f, ((ModelImporter)AssetImporter.GetAtPath(ModelPath)).globalScale);
        }

        [Test]
        public void Set_audio_importer_settings()
        {
            WriteAudio();
            Assert.IsInstanceOf<AudioImporter>(AssetImporter.GetAtPath(AudioPath));

            string answer = Ask("/set", "object", AudioPath, "importer", "true", "field", "forceToMono", "value", "true");
            TestContext.WriteLine(answer);
            StringAssert.StartsWith("{\"lines\":[\"" + AudioPath + "  AudioImporter.forceToMono  false -> true", answer);
            Assert.IsTrue(((AudioImporter)AssetImporter.GetAtPath(AudioPath)).forceToMono);
        }

        [Test]
        public void Set_material_properties_by_name()
        {
            WriteTexture();
            Ask("/asset/create", "path", Folder + "/Skin.mat", "type", "Material", "shader", "Unlit/Texture");
            string skin = Folder + "/Skin.mat";

            string answer = Ask("/set", "object", skin, "field", "_MainTex", "value", TexturePath);
            TestContext.WriteLine(answer);
            StringAssert.StartsWith("{\"lines\":[\"" + skin + "  _MainTex  ", answer);
            StringAssert.EndsWith("(" + skin + " modified, not saved)\"]}", answer);
            Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath), AssetDatabase.LoadAssetAtPath<Material>(skin).GetTexture("_MainTex"));

            Assert.AreEqual(
                Lines(skin + "  _MainTex  " + TexturePath + " -> null  (" + skin + " modified, not saved)"),
                Ask("/set", "object", skin, "field", "mainTex", "value", "null"));
            Assert.AreEqual(Err("no texture at " + Folder + "/Nope.png"), Ask("/set", "object", skin, "field", "_MainTex", "value", Folder + "/Nope.png"));
            Assert.AreEqual(Lines(skin + "  shader  Unlit/Texture -> Unlit/Color  (" + skin + " modified, not saved)"), Ask("/set", "object", skin, "field", "shader", "value", "Unlit/Color"));
        }

        private static void WriteTexture()
        {
            var texture = new Texture2D(4, 4);
            File.WriteAllBytes(TexturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(TexturePath);
        }

        // A 100-sample mono 16-bit 8 kHz WAV of silence.
        private static void WriteAudio()
        {
            const int samples = 100;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(8000);
                writer.Write(16000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                writer.Write(new byte[samples * 2]);
                writer.Flush();
                File.WriteAllBytes(AudioPath, stream.ToArray());
            }

            AssetDatabase.ImportAsset(AudioPath);
        }
    }
}
