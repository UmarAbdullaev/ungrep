using System;
using System.Collections.Generic;

namespace Game.Core
{
    public class Pool<T> where T : class
    {
        private readonly Stack<T> _items = new Stack<T>();

        public int Count => _items.Count;

        public T Get(Func<T> create)
        {
            T Make() => create();
            return _items.Count > 0 ? _items.Pop() : Make();
        }

        public void Release(T item) => _items.Push(item);

        public class Handle
        {
            public Pool<T> Owner;
        }
    }

    public struct Money
    {
        public long Cents;

        public Money(long cents) { Cents = cents; }

        public static Money operator +(Money a, Money b) => new Money(a.Cents + b.Cents);
    }
}
