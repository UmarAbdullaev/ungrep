using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>`/prefab/create|apply|revert|unpack` on the OpsFixture scene and Turret prefab (Barrel/Muzzle).</summary>
    public class PrefabOpsTests : OpsFixture
    {
        private const string HeroPath = Folder + "/Hero.prefab";
        private const string OuterPath = Folder + "/Outer.prefab";
        private const string Unsaved = "Main.unity modified, not saved)";

        [Test]
        public void Create_saves_the_object_as_a_prefab_and_keeps_it_linked()
        {
            Assert.AreEqual(
                Lines("created " + HeroPath + " from Level/Player  (written " + HeroPath + "; " + Unsaved),
                Ask("/prefab/create", "object", "Level/Player", "path", HeroPath));

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(HeroPath);
            Assert.IsNotNull(asset);
            Assert.IsNotNull(asset.transform.Find("Model"));
            Assert.IsNotNull(asset.GetComponent<UgProbe>());
            GameObject linked = Find("Level/Player");
            Assert.IsTrue(PrefabUtility.IsOutermostPrefabInstanceRoot(linked));
            Assert.AreEqual(HeroPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(linked));
            Assert.IsTrue(linked.scene.isDirty);

            Assert.AreEqual(Lines("undid ug prefab create"), Ask("/undo"));
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(Find("Level/Player")));
        }

        [Test]
        public void Create_from_an_instance_makes_a_variant()
        {
            Ask("/object/create", "prefab", PrefabPath);

            Assert.AreEqual(
                Lines("created " + HeroPath + " from Turret (variant of " + PrefabPath + ")  (written " + HeroPath + "; " + Unsaved),
                Ask("/prefab/create", "object", "Turret", "path", HeroPath));
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(HeroPath)));
        }

        [Test]
        public void Create_refuses_bad_paths_and_objects()
        {
            Assert.AreEqual(Err("no object given"), Ask("/prefab/create", "path", HeroPath));
            Assert.AreEqual(Err("no prefab path given"), Ask("/prefab/create", "object", "Level/Player"));
            Assert.AreEqual(Err(PrefabPath + " already exists: ug does not overwrite it"), Ask("/prefab/create", "object", "Level/Player", "path", PrefabPath));
            Assert.AreEqual(Err("the prefab path must end in .prefab: " + Folder + "/Hero.asset"), Ask("/prefab/create", "object", "Level/Player", "path", Folder + "/Hero.asset"));
            Assert.AreEqual(Err("the folder " + Folder + "/Nope does not exist"), Ask("/prefab/create", "object", "Level/Player", "path", Folder + "/Nope/Hero.prefab"));
            string outside = "the prefab path must be a project path under Assets/, like Assets/Prefabs/Crate.prefab: ";
            Assert.AreEqual(Err(outside + "Library/Hero.prefab"), Ask("/prefab/create", "object", "Level/Player", "path", "Library/Hero.prefab"));
            Assert.AreEqual(Err(outside + "Assets/../Hero.prefab"), Ask("/prefab/create", "object", "Level/Player", "path", "Assets/../Hero.prefab"));
            Assert.AreEqual(
                Err("Packages/com.example/Hero.prefab is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/prefab/create", "object", "Level/Player", "path", "Packages/com.example/Hero.prefab"));
            Assert.AreEqual(
                Err("Packages/com.example/Thing.prefab is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/prefab/create", "object", "Packages/com.example/Thing.prefab", "path", HeroPath));
            Assert.AreEqual(
                Err(HeroPath + " is in the vendored folder " + Folder + "/, which ug does not edit"),
                Ask("/prefab/create", "object", "Level/Player", "path", HeroPath, "vendored", "Assets/Plugins/|" + Folder + "/"));
            Assert.AreEqual(Err(PrefabPath + " is in a prefab asset, not a scene: ug asset duplicate copies an asset"), Ask("/prefab/create", "object", PrefabPath, "path", HeroPath));
            StringAssert.StartsWith("{\"error\":\"not found: Nobody in the loaded scenes", Ask("/prefab/create", "object", "Nobody", "path", HeroPath));

            Ask("/object/create", "prefab", PrefabPath);
            Assert.AreEqual(Err("Turret/Barrel is part of the prefab instance Turret: give Turret"), Ask("/prefab/create", "object", "Turret/Barrel", "path", HeroPath));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(HeroPath));
        }

        [Test]
        public void Apply_writes_the_overrides_into_the_prefab()
        {
            Ask("/object/create", "prefab", PrefabPath);
            Assert.AreEqual(Lines("no overrides on Turret to apply"), Ask("/prefab/apply", "object", "Turret"));

            Ask("/set", "object", "Turret", "field", "position", "value", "5,5,5");
            Ask("/set", "object", "Turret/Barrel", "field", "position", "value", "0,1,0");
            Ask("/component/add", "object", "Turret", "type", "Rigidbody");
            Ask("/object/create", "name", "Sight", "parent", "Turret/Barrel");

            string answer = Ask("/prefab/apply", "object", "Turret");
            TestContext.WriteLine(answer);
            StringAssert.StartsWith("{\"lines\":[\"applied 3 overrides from Turret to " + PrefabPath + "  (written " + PrefabPath + "; " + Unsaved + "\"", answer);
            StringAssert.Contains("Turret  added Rigidbody", answer);
            StringAssert.Contains("Turret/Barrel  Transform.m_LocalPosition", answer);
            StringAssert.Contains("Turret/Barrel/Sight  added object", answer);

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(asset.GetComponent<Rigidbody>());
            Assert.AreEqual(new Vector3(0, 1, 0), asset.transform.Find("Barrel").localPosition);
            Assert.IsNotNull(asset.transform.Find("Barrel/Sight"));
            Assert.AreEqual(new Vector3(5, 5, 5), Find("Turret").transform.position, "the instance's own position is not an override");
            Assert.IsFalse(PrefabUtility.HasPrefabInstanceAnyOverrides(Find("Turret"), false));
            Assert.AreEqual(Lines("no overrides on Turret to apply"), Ask("/prefab/apply", "object", "Turret"));
        }

        [Test]
        public void Overrides_lists_every_override_with_its_value_and_changes_nothing()
        {
            Ask("/object/create", "prefab", PrefabPath);
            Assert.AreEqual(Lines("Turret  instance of " + PrefabPath + "  no overrides"), Ask("/prefab/overrides", "object", "Turret"));

            Ask("/set", "object", "Turret/Barrel", "field", "position", "value", "0,1,0");
            Ask("/component/add", "object", "Turret", "type", "Rigidbody");
            Ask("/object/create", "name", "Sight", "parent", "Turret/Barrel");

            string answer = Ask("/prefab/overrides", "object", "Turret");
            TestContext.WriteLine(answer);
            StringAssert.StartsWith("{\"lines\":[\"Turret  instance of " + PrefabPath + "  3 overrides\"", answer);
            StringAssert.Contains("\"  Turret/Barrel  Transform.m_LocalPosition.y = 1\"", answer);
            StringAssert.Contains("\"  Turret  added Rigidbody\"", answer);
            StringAssert.Contains("\"  Turret/Barrel/Sight  added object\"", answer);
            Assert.IsTrue(PrefabUtility.HasPrefabInstanceAnyOverrides(Find("Turret"), false), "a read changes nothing");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Rigidbody>());

            Assert.AreEqual(Err("Level/Player is not a prefab instance: ug prefab create Level/Player <path> makes a prefab from it"), Ask("/prefab/overrides", "object", "Level/Player"));
            Assert.AreEqual(Err("Turret/Barrel is part of the prefab instance Turret: give Turret"), Ask("/prefab/overrides", "object", "Turret/Barrel"));
            Assert.AreEqual(Err("no object given"), Ask("/prefab/overrides"));
        }

        [Test]
        public void Past_forty_objects_revert_names_overrides_which_lists_them_all()
        {
            Ask("/object/create", "prefab", PrefabPath);
            for (int i = 0; i < 41; i++)
            {
                Ask("/object/create", "name", "Part" + i, "parent", "Turret");
            }

            string listed = Ask("/prefab/overrides", "object", "Turret");
            StringAssert.StartsWith("{\"lines\":[\"Turret  instance of " + PrefabPath + "  41 overrides\"", listed);
            StringAssert.Contains("\"  Turret/Part40  added object\"", listed);

            string reverted = Ask("/prefab/revert", "object", "Turret");
            StringAssert.EndsWith("\"... 1 more object (ug prefab overrides Turret lists every override before an apply or revert)\"]}", reverted);
            Assert.IsNull(GameObject.Find("Turret/Part40"));
        }

        [Test]
        public void Apply_is_one_undo_group()
        {
            Ask("/object/create", "prefab", PrefabPath);
            Ask("/component/add", "object", "Turret", "type", "Rigidbody");

            StringAssert.Contains("applied 1 override", Ask("/prefab/apply", "object", "Turret"));
            Assert.AreEqual(Lines("undid ug prefab apply"), Ask("/undo"));
        }

        [Test]
        public void Apply_refuses_vendored_folders_and_non_instances()
        {
            Ask("/object/create", "prefab", PrefabPath);
            Ask("/component/add", "object", "Turret", "type", "Rigidbody");

            Assert.AreEqual(
                Err(PrefabPath + " is in the vendored folder " + Folder + "/, which ug does not edit"),
                Ask("/prefab/apply", "object", "Turret", "vendored", "Assets/Plugins/|" + Folder + "/"));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Rigidbody>());

            Assert.AreEqual(Err("Level/Player is not a prefab instance: ug prefab create Level/Player <path> makes a prefab from it"), Ask("/prefab/apply", "object", "Level/Player"));
            Assert.AreEqual(Err("Turret/Barrel is part of the prefab instance Turret: give Turret"), Ask("/prefab/apply", "object", "Turret/Barrel"));
            Assert.AreEqual(Err(PrefabPath + " is in a prefab asset: prefab apply works on prefab instances in the loaded scenes"), Ask("/prefab/apply", "object", PrefabPath));
            Assert.AreEqual(Err("no object given"), Ask("/prefab/apply"));
            Assert.AreEqual(
                Err("Packages/com.example/Thing.prefab is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/prefab/apply", "object", "Packages/com.example/Thing.prefab"));
        }

        [Test]
        public void Revert_throws_the_overrides_away()
        {
            Ask("/object/create", "prefab", PrefabPath);
            Assert.AreEqual(Lines("no overrides on Turret to revert"), Ask("/prefab/revert", "object", "Turret"));

            Ask("/set", "object", "Turret", "field", "position", "value", "5,5,5");
            Ask("/set", "object", "Turret/Barrel", "field", "position", "value", "0,1,0");
            Ask("/component/add", "object", "Turret", "type", "Rigidbody");

            string answer = Ask("/prefab/revert", "object", "Turret");
            TestContext.WriteLine(answer);
            StringAssert.StartsWith("{\"lines\":[\"reverted 2 overrides on Turret  (" + Unsaved + "\"", answer);
            StringAssert.Contains("Turret  added Rigidbody", answer);
            StringAssert.Contains("Turret/Barrel  Transform.m_LocalPosition", answer);
            Assert.AreEqual(Vector3.zero, Find("Turret/Barrel").transform.localPosition);
            Assert.AreEqual(new Vector3(5, 5, 5), Find("Turret").transform.position, "the instance's own position is not an override");
            Assert.IsNull(Find("Turret").GetComponent<Rigidbody>());
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<Rigidbody>());

            Ask("/component/add", "object", "Turret", "type", "Rigidbody");
            Ask("/prefab/revert", "object", "Turret");
            Assert.AreEqual(Lines("undid ug prefab revert"), Ask("/undo"));
            Assert.IsNotNull(Find("Turret").GetComponent<Rigidbody>());

            Assert.AreEqual(Err("Level/Player is not a prefab instance: ug prefab create Level/Player <path> makes a prefab from it"), Ask("/prefab/revert", "object", "Level/Player"));
            Assert.AreEqual(Err("Turret/Barrel is part of the prefab instance Turret: give Turret"), Ask("/prefab/revert", "object", "Turret/Barrel"));
        }

        [Test]
        public void Unpack_cuts_the_link_one_level_or_completely()
        {
            var inner = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            var outer = new GameObject("Outer");
            inner.transform.SetParent(outer.transform);
            PrefabUtility.SaveAsPrefabAsset(outer, OuterPath);
            Object.DestroyImmediate(outer);
            Ask("/object/create", "prefab", OuterPath);

            Assert.AreEqual(Lines("unpacked Outer from " + OuterPath + "  (" + Unsaved), Ask("/prefab/unpack", "object", "Outer"));
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(Find("Outer")));
            Assert.IsTrue(PrefabUtility.IsOutermostPrefabInstanceRoot(Find("Outer/Turret")), "the nested instance stays linked");
            Assert.AreEqual(Lines("undid ug prefab unpack"), Ask("/undo"));
            Assert.IsTrue(PrefabUtility.IsOutermostPrefabInstanceRoot(Find("Outer")));

            Assert.AreEqual(Lines("unpacked Outer from " + OuterPath + " completely  (" + Unsaved), Ask("/prefab/unpack", "object", "Outer", "completely", "true"));
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(Find("Outer/Turret")));

            Assert.AreEqual(Err("Outer is not a prefab instance: ug prefab create Outer <path> makes a prefab from it"), Ask("/prefab/unpack", "object", "Outer"));
            Assert.AreEqual(Err("no object given"), Ask("/prefab/unpack"));
        }
    }
}
