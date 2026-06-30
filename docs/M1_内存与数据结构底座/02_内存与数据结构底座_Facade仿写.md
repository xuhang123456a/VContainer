# M1 内存复用与数据结构底座 · Facade 仿写

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| `CappedArrayPool<T>` 多桶 + lock | 保留按长度分桶 + 栈顶 tail + lock | **保留**（核心不变量） |
| 桶满 `Array.Resize ×2` | 保留 | **保留** |
| `>maxLength` 直接 new | 保留 | **保留** |
| `FreeList<T>` unsafe IndexOf | 退化为 `for + ==null` | **简化**（行为等价） |
| `FreeList` ×1.5 扩容 / `lastIndex` | 保留 | **保留**（迭代安全的关键） |
| `ListPool<T>` + `BufferScope` | 保留 | **保留** |
| `FixedTypeObjectKeyHashtable` 拉链 | 退化为 `Dictionary` 包装，但保留「冻结只读」语义 | **简化** |
| `TypeKeyHashTable2` Robin Hood | 砍掉（运行时未使用） | **砍掉** |
| `CompositeDisposable` LIFO | 保留 | **保留** |
| Unity unsafe / 多 TFM 条件编译 | 砍掉 | **砍掉** |

## 最小可编译复刻

```csharp
using System;
using System.Collections.Generic;

namespace MiniVC.Foundation
{
    // 1) 按精确长度分桶的数组池：Rent 永远返回恰好 length 长的数组
    public sealed class CappedArrayPool<T>
    {
        const int InitialBucketSize = 4;
        public static readonly CappedArrayPool<T> Shared8 = new CappedArrayPool<T>(8);

        readonly T[][][] buckets;
        readonly int[] tails;
        readonly object gate = new object();

        public CappedArrayPool(int maxLength)
        {
            buckets = new T[maxLength][][];
            tails = new int[maxLength];
            for (var i = 0; i < maxLength; i++)
            {
                buckets[i] = new T[InitialBucketSize][];
                for (var j = 0; j < InitialBucketSize; j++)
                    buckets[i][j] = new T[i + 1];
            }
        }

        public T[] Rent(int length)
        {
            if (length <= 0) return Array.Empty<T>();
            if (length > buckets.Length) return new T[length];   // 超界不入池
            var i = length - 1;
            lock (gate)
            {
                var bucket = buckets[i];
                var tail = tails[i];
                if (tail >= bucket.Length)                        // 桶满翻倍
                {
                    Array.Resize(ref bucket, bucket.Length * 2);
                    buckets[i] = bucket;
                }
                bucket[tail] ??= new T[length];
                tails[i]++;
                return bucket[tail];
            }
        }

        public void Return(T[] array)
        {
            if (array.Length <= 0 || array.Length > buckets.Length) return;
            var i = array.Length - 1;
            lock (gate)
            {
                Array.Clear(array, 0, array.Length);              // 清引用防泄漏
                if (tails[i] > 0) tails[i]--;
            }
        }
    }

    // 2) 索引稳定、迭代期可安全删除的空闲表
    public sealed class FreeList<T> where T : class
    {
        readonly object gate = new object();
        T[] values;
        int lastIndex = -1;
        public int Length => lastIndex + 1;
        public T this[int index] => values[index];

        public FreeList(int cap) => values = new T[cap];

        public void Add(T item)
        {
            lock (gate)
            {
                var index = IndexOfNull(values);
                if (index == -1)                                  // 满则 ×1.5
                {
                    var len = values.Length;
                    Array.Resize(ref values, len + len / 2);
                    index = len;
                }
                values[index] = item;
                if (lastIndex < index) lastIndex = index;
            }
        }

        public void RemoveAt(int index)
        {
            lock (gate)
            {
                if (index >= values.Length || values[index] == null) return;
                values[index] = null;                             // 原位 null，不搬移
                if (index == lastIndex)
                    for (lastIndex = index - 1; lastIndex >= 0 && values[lastIndex] == null; lastIndex--) { }
            }
        }

        static int IndexOfNull(T[] a)
        {
            for (var i = 0; i < a.Length; i++) if (a[i] == null) return i;
            return -1;
        }
    }

    // 3) List<T> 复用池 + using 作用域回收
    public static class ListPool<T>
    {
        static readonly Stack<List<T>> pool = new Stack<List<T>>();
        public readonly struct Scope : IDisposable
        {
            readonly List<T> buf;
            public Scope(List<T> b) => buf = b;
            public void Dispose() => Release(buf);
        }
        public static List<T> Get() { lock (pool) return pool.Count > 0 ? pool.Pop() : new List<T>(32); }
        public static Scope Get(out List<T> buf) { buf = Get(); return new Scope(buf); }
        public static void Release(List<T> b) { b.Clear(); lock (pool) pool.Push(b); }
    }

    // 4) LIFO 释放栈
    public sealed class CompositeDisposable : IDisposable
    {
        readonly Stack<IDisposable> items = new Stack<IDisposable>();
        public void Add(IDisposable d) { lock (items) items.Push(d); }
        public void Dispose()
        {
            IDisposable d;
            do { lock (items) d = items.Count > 0 ? items.Pop() : null; d?.Dispose(); }
            while (d != null);
        }
    }
}
```

## 使用示例

```csharp
using MiniVC.Foundation;

// 池：恰好长度 + try/finally 配对
var args = CappedArrayPool<object>.Shared8.Rent(2);
try { args[0] = "a"; args[1] = 42; /* MethodInfo.Invoke(args) */ }
finally { CappedArrayPool<object>.Shared8.Return(args); }

// FreeList：迭代中删除
var loop = new FreeList<Action>(4);
loop.Add(() => Console.WriteLine("tick"));
for (var i = 0; i < loop.Length; i++)
{
    var item = loop[i];
    if (item == null) continue;     // 跳过被删空洞
    item();
    if (/*done*/ true) loop.RemoveAt(i);  // 安全：原位 null
}

// ListPool：using 自动归还
using (ListPool<int>.Get(out var buffer))
{
    buffer.Add(1); buffer.Add(2);
}   // 离开作用域即清空压回池
```

## 取舍自检

- ✅ **保留**：按精确长度分桶（服务于反射 invoke 的定长参数数组）、FreeList 索引稳定 + null 空洞、ListPool 的 `BufferScope` using 回收、CompositeDisposable 的 LIFO。
- ❌ **砍掉**：`TypeKeyHashTable2`（运行时无调用点）、Unity unsafe 找空洞、多 TFM 条件编译、`FixedTypeObjectKeyHashtable` 的手写拉链（用 Dictionary 等价即可）。
- ⚠️ **最容易搞错**：把 `Rent` 来的数组**存进字段或跨越 `Return` 使用**。池不追踪归属，`Return` 会 `Array.Clear`；若别处还持有该引用，会在运行中被清空——表现为「字段莫名变 null」。务必让租借生命周期严格包在 `try/finally` 内。
