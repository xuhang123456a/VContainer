# M6 入口点与 PlayerLoop 调度 · Facade 仿写

> 说明：用纯 C# 模拟"主循环驱动 + 一次性/持续性入口点 + 容错"。用 `Tick()` 手动驱动代替 Unity PlayerLoop，复用 M1 的 `FreeList` 不变量。

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| Unity PlayerLoop 10 阶段插入 | 退化为单一 `LoopRunner.Tick()` 手动驱动 | **简化** |
| `IPlayerLoopItem.MoveNext` 一次性/持续语义 | 保留 | **保留**（核心不变量） |
| `FreeList` 承载 + 迭代安全删除 | 复用 M1 FreeList | **保留** |
| Init 立即执行 / Tick 挂载 | 保留 | **保留** |
| `*LoopItem` try/catch + handler | 保留 | **保留** |
| `ContainerLocal<IReadOnlyList<T>>` 作用域隔离 | 退化为直接传入集合 | **简化** |
| Async/UniTask 入口点 | 砍掉 | **砍掉** |
| 进程级 EnsureInitialized | 砍掉 | **砍掉** |

## 最小可编译复刻

```csharp
using System;
using System.Collections.Generic;
using MiniVC.Foundation;   // 复用 M1 的 FreeList

namespace MiniVC.EntryPoints
{
    // ---- 入口点契约 ----
    public interface IInitializable { void Initialize(); }
    public interface IStartable { void Start(); }
    public interface ITickable { void Tick(); }

    public sealed class ExceptionHandler
    {
        readonly Action<Exception> handler;
        public ExceptionHandler(Action<Exception> h) => handler = h;
        public void Publish(Exception e) => handler(e);
    }

    // ---- PlayerLoopItem：返回值表达一次性/持续性 ----
    public interface ILoopItem { bool MoveNext(); }

    public sealed class StartableLoopItem : ILoopItem, IDisposable
    {
        readonly IEnumerable<IStartable> entries; readonly ExceptionHandler eh; bool disposed;
        public StartableLoopItem(IEnumerable<IStartable> e, ExceptionHandler h) { entries = e; eh = h; }
        public bool MoveNext()
        {
            if (disposed) return false;
            foreach (var x in entries)
                try { x.Start(); }
                catch (Exception ex) { if (eh == null) throw; eh.Publish(ex); }
            return false;                              // 一次性：执行后出列
        }
        public void Dispose() => disposed = true;
    }

    public sealed class TickableLoopItem : ILoopItem, IDisposable
    {
        readonly IReadOnlyList<ITickable> entries; readonly ExceptionHandler eh; bool disposed;
        public TickableLoopItem(IReadOnlyList<ITickable> e, ExceptionHandler h) { entries = e; eh = h; }
        public bool MoveNext()
        {
            if (disposed) return false;
            for (var i = 0; i < entries.Count; i++)
                try { entries[i].Tick(); }
                catch (Exception ex) { if (eh == null) throw; eh.Publish(ex); }
            return !disposed;                          // 持续：每帧执行
        }
        public void Dispose() => disposed = true;
    }

    // ---- 单阶段的帧驱动器（复刻 PlayerLoopRunner）----
    public sealed class LoopRunner
    {
        readonly FreeList<ILoopItem> runners = new FreeList<ILoopItem>(16);
        public void Dispatch(ILoopItem item) => runners.Add(item);
        public void Tick()                              // 等价 Run()
        {
            for (var i = 0; i < runners.Length; i++)
            {
                var item = runners[i];
                if (item == null) continue;            // 跳过空洞
                if (!item.MoveNext()) runners.RemoveAt(i); // 原位 null，迭代安全
            }
        }
    }

    // ---- 调度器（复刻 EntryPointDispatcher）----
    public sealed class EntryPointDispatcher : IDisposable
    {
        readonly CompositeDisposable disposable = new CompositeDisposable();
        readonly LoopRunner startupRunner, updateRunner;

        public EntryPointDispatcher(LoopRunner startup, LoopRunner update)
        { startupRunner = startup; updateRunner = update; }

        public void Dispatch(
            IReadOnlyList<IInitializable> initializables,
            IReadOnlyList<IStartable> startables,
            IReadOnlyList<ITickable> tickables,
            ExceptionHandler eh)
        {
            // 1) Init：立即同步执行
            for (var i = 0; i < initializables.Count; i++)
                try { initializables[i].Initialize(); }
                catch (Exception ex) { if (eh != null) eh.Publish(ex); else throw; }

            // 2) Startable：挂载（一次性）
            if (startables.Count > 0)
            {
                var item = new StartableLoopItem(startables, eh);
                disposable.Add(item);
                startupRunner.Dispatch(item);
            }
            // 3) Tickable：挂载（持续）
            if (tickables.Count > 0)
            {
                var item = new TickableLoopItem(tickables, eh);
                disposable.Add(item);
                updateRunner.Dispatch(item);
            }
        }

        public void Dispose() => disposable.Dispose();  // 置 disposed，下帧自动出列
    }
}
```

## 使用示例

```csharp
var startup = new LoopRunner();
var update = new LoopRunner();
var dispatcher = new EntryPointDispatcher(startup, update);

var eh = new ExceptionHandler(Console.WriteLine);  // 等价默认 Debug.LogException
dispatcher.Dispatch(
    initializables: new IInitializable[] { new Boot() },   // 立即跑
    startables:     new IStartable[]     { new Boot() },   // 下个 startup 帧跑一次
    tickables:      new ITickable[]      { new Player() }, // 每个 update 帧跑
    eh);

// 模拟主循环
for (var frame = 0; frame < 3; frame++) { startup.Tick(); update.Tick(); }

dispatcher.Dispose();   // Player 在下一帧 Tick 时自动出列
update.Tick();          // Player.Tick 不再被调用
```

## 取舍自检

- ✅ **保留**：`MoveNext` 返回值表达"一次性(false)/持续(!disposed)"、FreeList 迭代安全删除、Init 立即/Tick 挂载、try/catch + handler 容错、Dispose 置标志靠下帧出列。
- ❌ **砍掉**：Unity PlayerLoop 进程级注入、10 个细分阶段、Async/UniTask 入口点、`ContainerLocal` 作用域隔离（直接传集合）。
- ⚠️ **最容易搞错**：`Dispose` 时**不要**直接从 `FreeList` 删 item，而应置 `disposed=true`、靠下一帧 `MoveNext` 返回 false 由 `Run/Tick` 来 `RemoveAt`。若在 Dispose 里直接 RemoveAt，可能与正在进行的 `Tick` 遍历竞态，破坏 FreeList 的"当前帧索引稳定"前提。
