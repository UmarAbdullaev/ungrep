using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Ungrep.Bridge.Tests
{
    public static class ApiDefaultsFixture
    {
        public static void Shoot(int power, float range = float.PositiveInfinity, Space space = Space.Self, string label = "x")
        {
        }
    }

    // Nine public types named ApiDupC1, one more than an ambiguous name lists.
    public static class ApiDupHolders
    {
        public static class A { public class ApiDupC1 { } }
        public static class B { public class ApiDupC1 { } }
        public static class C { public class ApiDupC1 { } }
        public static class D { public class ApiDupC1 { } }
        public static class E { public class ApiDupC1 { } }
        public static class F { public class ApiDupC1 { } }
        public static class G { public class ApiDupC1 { } }
        public static class H { public class ApiDupC1 { } }
        public static class I { public class ApiDupC1 { } }
    }

    /// <summary>`/api`: reflection answers for common types, small; and the pointers to documentation pages when reflection cannot answer.</summary>
    public class ApiOpsTests : OpsFixture
    {
        private Func<string> _docsRoot;
        private string _docs;

        [SetUp]
        public void PointDocsAtAnEmptyFolder()
        {
            _docsRoot = ApiOps.DocsRoot;
            _docs = Path.Combine(Path.GetTempPath(), "ug-docs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_docs);
            ApiOps.DocsRoot = () => _docs;
        }

        [TearDown]
        public void RestoreDocs()
        {
            ApiOps.DocsRoot = _docsRoot;
            Directory.Delete(_docs, true);
        }

        private static string Version => string.Join(".", Application.unityVersion.Split('.').Take(2));

        [Test]
        public void A_type_lists_its_base_assembly_and_member_names()
        {
            string body = Ask("/api", "type", "Transform");

            StringAssert.StartsWith("{\"lines\":[\"class UnityEngine.Transform : Component  (UnityEngine.CoreModule)\",", body);
            StringAssert.Contains("\"props  position, localPosition, ", body);
            StringAssert.Contains("\"methods  ", body);
            StringAssert.Contains("Translate", body);
            StringAssert.Contains("\"ug api Transform.<member> for signatures (inherited members too); the rest: ug api Transform --all\"]", body);
        }

        [TestCase("Transform")]
        [TestCase("GameObject")]
        [TestCase("Rigidbody")]
        [TestCase("Rigidbody2D")]
        [TestCase("Camera")]
        [TestCase("Collider")]
        [TestCase("Vector3")]
        [TestCase("Physics")]
        [TestCase("Time")]
        [TestCase("MonoBehaviour")]
        [TestCase("Mathf")]
        [TestCase("Input")]
        public void Common_types_answer_in_under_300_tokens(string type)
        {
            string body = Ask("/api", "type", type);

            StringAssert.StartsWith("{\"lines\":[", body);
            int tokens = Tokens(body);
            TestContext.WriteLine("api " + type + ": " + tokens + " tokens");
            Assert.LessOrEqual(tokens, 300, type + " answered " + tokens + " tokens: " + body);
        }

        // The estimate the query answers use: 4 UTF-8 bytes to a token, on the printed lines.
        private static int Tokens(string body)
        {
            string printed = string.Join("\n", body.Substring("{\"lines\":[\"".Length).TrimEnd('}', ']', '"').Split(new[] { "\",\"" }, StringSplitOptions.None));
            return (Encoding.UTF8.GetByteCount(printed) + 3) / 4;
        }

        [Test]
        public void A_type_finds_by_full_name_and_by_case_insensitive_name()
        {
            StringAssert.StartsWith("{\"lines\":[\"class UnityEngine.Transform : Component", Ask("/api", "type", "UnityEngine.Transform"));
            StringAssert.StartsWith("{\"lines\":[\"class UnityEngine.Transform : Component", Ask("/api", "type", "transform"));
            StringAssert.StartsWith("{\"lines\":[\"class UnityEngine.GameObject : Object", Ask("/api", "type", "GameObject"));
        }

        [Test]
        public void An_enum_lists_its_values_and_a_struct_is_named_as_one()
        {
            StringAssert.StartsWith("{\"lines\":[\"enum UnityEngine.Space  (UnityEngine.CoreModule)\",\"values  World, Self\"]", Ask("/api", "type", "Space"));
            StringAssert.StartsWith("{\"lines\":[\"struct UnityEngine.Vector3", Ask("/api", "type", "Vector3"));
            StringAssert.StartsWith("{\"lines\":[\"struct UnityEngine.Mathf", Ask("/api", "type", "Mathf"));
        }

        [Test]
        public void A_method_lists_its_overloads()
        {
            string body = Ask("/api", "type", "Transform", "member", "Rotate");

            StringAssert.StartsWith("{\"lines\":[\"UnityEngine.Transform.Rotate\",", body);
            StringAssert.Contains("\"  void Rotate(Vector3 eulers, Space relativeTo)\"", body);
            StringAssert.Contains("\"  void Rotate(Vector3 eulers)\"", body);
            StringAssert.Contains("\"  void Rotate(float xAngle, float yAngle, float zAngle)\"", body);
        }

        [Test]
        public void Parameters_show_their_defaults_when_the_method_has_them()
        {
            string body = Ask("/api", "type", "Ungrep.Bridge.Tests.ApiDefaultsFixture", "member", "Shoot");

            StringAssert.Contains("float range = Infinity", body);
            StringAssert.Contains("Space space = Space.Self", body);
            StringAssert.Contains("string label = \\\"x\\\"", body);
        }

        [Test]
        public void A_property_shows_its_type_and_accessors()
        {
            StringAssert.Contains("\"  Vector3 position { get; set; }\"", Ask("/api", "type", "Transform", "member", "position"));
            StringAssert.Contains("\"  int childCount { get; }\"", Ask("/api", "type", "Transform", "member", "childCount"));
        }

        [Test]
        public void Members_include_inherited_ones_and_say_where_from()
        {
            StringAssert.Contains("\"  string name { get; set; }  (from Object)\"", Ask("/api", "type", "Rigidbody", "member", "name"));
        }

        [Test]
        public void Type_dot_member_in_one_word_is_split_when_no_type_has_that_name()
        {
            string body = Ask("/api", "type", "Physics.Raycast");

            StringAssert.StartsWith("{\"lines\":[\"UnityEngine.Physics.Raycast\",", body);
            StringAssert.Contains("static bool Raycast(Vector3 origin, Vector3 direction", body);
            Assert.AreEqual(Ask("/api", "type", "Physics", "member", "Raycast"), body);
        }

        [Test]
        public void Members_match_without_regard_to_case_and_show_out_and_default_parameters()
        {
            StringAssert.Contains("Vector3 position { get; set; }", Ask("/api", "type", "Transform", "member", "POSITION"));
            StringAssert.Contains("out RaycastHit hitInfo", Ask("/api", "type", "Physics", "member", "Raycast"));
        }

        [Test]
        public void A_missing_member_suggests_near_ones()
        {
            Assert.AreEqual(Err("no member positon on UnityEngine.Transform (near: position)"), Ask("/api", "type", "Transform", "member", "positon"));
        }

        [Test]
        public void Names_that_two_unity_types_share_are_ambiguous_and_listed()
        {
            string shared = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch (System.Reflection.ReflectionTypeLoadException e)
                    {
                        return e.Types.Where(t => t != null).ToArray();
                    }
                })
                .Where(t => t.IsPublic && t.Namespace != null && (t.Namespace.StartsWith("UnityEngine") || t.Namespace.StartsWith("UnityEditor")) && !t.IsGenericType)
                .GroupBy(t => t.Name)
                .Where(g => g.Count() > 1 && g.Select(t => t.FullName).Distinct().Count() > 1)
                .Select(g => g.Key)
                .FirstOrDefault();
            if (shared == null)
            {
                Assert.Ignore("no simple name is shared by two public Unity types here");
            }

            string body = Ask("/api", "type", shared);

            StringAssert.StartsWith("{\"error\":\"ambiguous: " + shared + " (", body);
            StringAssert.Contains("; use the full name", body);
        }

        [Test]
        public void Unity_types_win_over_system_types_of_the_same_name()
        {
            StringAssert.StartsWith("{\"lines\":[\"class UnityEngine.Object", Ask("/api", "type", "Object"));
        }

        [Test]
        public void An_unknown_type_points_at_the_online_page_when_no_local_docs_exist()
        {
            string body = Ask("/api", "type", "NoSuchThingAtAll");

            Assert.AreEqual(
                "{\"error\":\"no type NoSuchThingAtAll in the loaded assemblies\",\"online\":[\"https://docs.unity3d.com/" + Version + "/Documentation/ScriptReference/NoSuchThingAtAll.html\"]}",
                body);
        }

        [Test]
        public void An_unknown_member_of_an_unknown_type_offers_both_page_names()
        {
            string body = Ask("/api", "type", "NoSuchThing", "member", "Go");

            StringAssert.Contains("ScriptReference/NoSuchThing.Go.html\",\"https://docs.unity3d.com/" + Version + "/Documentation/ScriptReference/NoSuchThing-Go.html\"]", body);
        }

        [Test]
        public void A_local_scripting_reference_page_is_named_instead_of_the_online_one()
        {
            string page = Path.Combine(_docs, "ScriptReference", "NoSuchThingAtAll.html");
            Directory.CreateDirectory(Path.GetDirectoryName(page));
            File.WriteAllText(page, "<html></html>");

            Assert.AreEqual(new Json().Object().Field("local", page).EndObject().ToString(), Ask("/api", "type", "NoSuchThingAtAll"));
        }

        [Test]
        public void Manual_pages_default_the_folder_and_point_online_or_local()
        {
            string online = "{\"error\":\"Manual/Prefabs is not in the local documentation\",\"online\":[\"https://docs.unity3d.com/" + Version + "/Documentation/Manual/Prefabs.html\"]}";
            Assert.AreEqual(online, Ask("/api", "page", "Manual/Prefabs"));
            Assert.AreEqual(online, Ask("/api", "page", "Prefabs"));
            Assert.AreEqual(online, Ask("/api", "page", "/Manual/Prefabs.html"));

            string page = Path.Combine(_docs, "Manual", "Prefabs.html");
            Directory.CreateDirectory(Path.GetDirectoryName(page));
            File.WriteAllText(page, "<html></html>");
            Assert.AreEqual(new Json().Object().Field("local", page).EndObject().ToString(), Ask("/api", "page", "Manual/Prefabs"));
        }

        [Test]
        public void Package_pages_need_a_version_the_project_or_the_page_gives()
        {
            Assert.AreEqual(
                "{\"error\":\"com.unity.cinemachine@3.1/manual/index has no offline copy\",\"online\":[\"https://docs.unity3d.com/Packages/com.unity.cinemachine@3.1/manual/index.html\"]}",
                Ask("/api", "page", "com.unity.cinemachine@3.1/manual/index"));
            StringAssert.Contains("\"online\":[\"https://docs.unity3d.com/Packages/com.unity.cinemachine@3.1/manual/index.html\"]", Ask("/api", "page", "com.unity.cinemachine@3.1"));
            Assert.AreEqual(
                Err("com.not.installed is not installed, so its documentation version is unknown: give one, like com.not.installed@1.2/manual/index"),
                Ask("/api", "page", "com.not.installed/manual/index"));
        }

        [Test]
        public void Package_pages_of_an_installed_package_use_its_major_and_minor_version()
        {
            string body = Ask("/api", "page", "com.unity.test-framework/manual/index");

            StringAssert.Contains("\"online\":[\"https://docs.unity3d.com/Packages/com.unity.test-framework@1.", body);
            StringAssert.EndsWith("/manual/index.html\"]}", body);
        }

        [TestCase("../Manual/Prefabs")]
        [TestCase("Manual/../../etc/passwd")]
        [TestCase("Manual/a b")]
        [TestCase("Manual/x?y=1")]
        [TestCase("Manual//x")]
        public void Page_names_that_are_not_names_are_refused(string page)
        {
            StringAssert.StartsWith("{\"error\":\"not a documentation page: ", Ask("/api", "page", page));
        }

        [Test]
        public void A_section_without_a_page_is_refused()
        {
            Assert.AreEqual(Err("give a page after Manual/: like Manual/Prefabs"), Ask("/api", "page", "Manual"));
        }

        [Test]
        public void Bad_or_missing_names_are_refused()
        {
            Assert.AreEqual(Err("give a type: ug api Transform, ug api Transform.position, ug api UnityEngine.Physics.Raycast"), Ask("/api"));
            Assert.AreEqual(Err("not a type or member name: a b"), Ask("/api", "type", "a b"));
            Assert.AreEqual(Err("not a type or member name: Transform.x y"), Ask("/api", "type", "Transform", "member", "x y"));
        }

        [Test]
        public void A_cut_type_names_all_which_lists_every_name()
        {
            string cut = Ask("/api", "type", "Transform");
            string all = Ask("/api", "type", "Transform", "all", "true");

            StringAssert.Contains(" more\"", cut);
            StringAssert.DoesNotContain("GetSiblingIndex", cut);
            StringAssert.EndsWith("\"ug api Transform.<member> for signatures (inherited members too); the rest: ug api Transform --all\"]}", cut);
            StringAssert.DoesNotContain(" more", all);
            StringAssert.EndsWith("\"ug api Transform.<member> for signatures (inherited members too)\"]}", all);
            foreach (string name in new[] { "DetachChildren", "SetSiblingIndex", "GetSiblingIndex", "IsChildOf", "SetAsLastSibling" })
            {
                StringAssert.Contains(name, all);
            }
        }

        [Test]
        public void A_cut_enum_says_which_command_lists_every_value()
        {
            string cut = Ask("/api", "type", "KeyCode");
            string all = Ask("/api", "type", "KeyCode", "all", "true");

            StringAssert.EndsWith(" more\",\"every value: ug api KeyCode --all\"]}", cut);
            StringAssert.DoesNotContain(" more", all);
            StringAssert.Contains("Joystick8Button19", all);
        }

        [Test]
        public void Past_25_overloads_the_cut_line_names_all()
        {
            string cut = Ask("/api", "type", "System.Convert", "member", "ToString");
            string all = Ask("/api", "type", "System.Convert", "member", "ToString", "all", "true");
            int overloads = typeof(Convert).GetMethods().Count(m => m.Name == "ToString" && m.IsPublic);

            StringAssert.EndsWith("\"  +" + (overloads - 25) + " more: ug api Convert.ToString --all\"]}", cut);
            Assert.AreEqual(overloads + 1, all.Split(new[] { "\",\"" }, StringSplitOptions.None).Length, all);
        }

        [Test]
        public void An_ambiguous_name_past_eight_types_names_all_which_lists_every_one()
        {
            string cut = Ask("/api", "type", "ApiDupC1");
            string all = Ask("/api", "type", "ApiDupC1", "all", "true");

            StringAssert.StartsWith("{\"error\":\"ambiguous: ApiDupC1 (9 types): ", cut);
            StringAssert.EndsWith(", ... 1 more: ug api ApiDupC1 --all; use the full name\"}", cut);
            StringAssert.Contains("ApiDupHolders+I+ApiDupC1", all);
            StringAssert.EndsWith("ApiDupC1; use the full name\"}", all);
        }

        [Test]
        public void Api_changes_nothing()
        {
            Ask("/api", "type", "Transform");
            Ask("/api", "type", "Transform", "member", "Rotate");

            Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty);
        }
    }
}
