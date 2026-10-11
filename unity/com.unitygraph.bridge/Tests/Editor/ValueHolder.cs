using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGraph.Bridge.Tests
{
    public interface IShape
    {
    }

    [Serializable]
    public class Circle : IShape
    {
        public float radius = 1f;
        public string label = "";
    }

    [Serializable]
    public class Square : IShape
    {
        public float side = 2f;
    }

    [Serializable]
    public struct Pair
    {
        public int count;
        public Vector3 offset;
        public string name;
    }

    [Serializable]
    public struct Nest
    {
        public Pair inner;
        public int level;
    }

    /// <summary>Every kind of field `ug set` reads from text, for ValuesCompoundTests.</summary>
    public class ValueHolder : ScriptableObject
    {
        public Bounds bounds;
        public AnimationCurve curve = new AnimationCurve();
        public Gradient gradient = new Gradient();
        public Hash128 hash;
        [SerializeReference] public IShape shape;
        public Pair pair;
        public Vector3[] points;
        public string[] names;
        public List<Pair> pairs;
        public int[] numbers;
        public Nest nest;
    }
}
