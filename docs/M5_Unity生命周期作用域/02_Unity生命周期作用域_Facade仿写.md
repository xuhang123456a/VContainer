# M5 Unity 生命周期作用域 · Facade 仿写

> 说明：本 Facade 用纯 C# 模拟「宿主 + 生命周期事件 + 等待队列」的骨架，不依赖 UnityEngine（用 `OnAwake()/OnDestroy()` 方法代替 MonoBehaviour 回调，用 `IHostContainer` 代替 M4 容器）。核心是复刻**父定位优先级链 + 父未就绪的等待重试 + 回调式回填**这三个不变量。

## 设计映射表

| 原实现 | 精简版 | 处理 |
|---|---|---|
| `MonoBehaviour` + `[DefaultExecutionOrder]` | `Scope` 普通类 + 手动 `OnAwake()` | **简化**（脱 Unity） |
| `GetRuntimeParent` 6 级优先级 | 保留「显式引用 → 类型查找 → 全局栈」 | **保留**（核心） |
| 父未就绪等待队列 + 异常重试 | 保留 `WaitingList` + `EnqueueAwake` | **保留**（最有价值） |
| `RegisterBuildCallback(SetContainer)` | 保留回调回填 | **保留** |
| `InstallTo`：Configure+安装器+自注册 | 保留安装器汇聚 | **保留** |
| `ParentReference` 序列化 | 退化为直接持 `Type`/`Scope` | **简化** |
| `VContainerSettings` Root 跨场景 | 退化为静态 `Root` 字段 | **简化** |
| 全局 Override/ExtraInstaller 栈 | 保留 `using` 作用域压栈 | **保留** |
| EntryPoint 调度注册 | 砍掉（见 M6） | **砍掉** |

## 最小可编译复刻

```csharp
using System;
using System.Collections.Generic;

namespace MiniVC.Hosting
{
    // 抽象掉 M4 容器
    public interface IHostContainer
    {
        IHostContainer CreateScope(Action<IBuilder> install, Action<IHostContainer> onBuilt);
        void Dispose();
    }
    public interface IBuilder { void RegisterInstance(object instance); void OnBuilt(Action<IHostContainer> cb); }
    public interface IInstaller { void Install(IBuilder b); }
    public sealed class ActionInstaller : IInstaller
    {
        readonly Action<IBuilder> cfg;
        public ActionInstaller(Action<IBuilder> c) => cfg = c;
        public static implicit operator ActionInstaller(Action<IBuilder> c) => new ActionInstaller(c);
        public void Install(IBuilder b) => cfg(b);
    }

    public sealed class ParentTypeNotFound : Exception { public ParentTypeNotFound(string m) : base(m) { } }

    public class Scope
    {
        // ---- 全局栈（using 作用域）----
        static readonly Stack<Scope> GlobalParents = new();
        static readonly Stack<IInstaller> GlobalInstallers = new();
        static readonly List<Scope> WaitingList = new();
        static readonly object Sync = new();
        static Func<Action<IBuilder>, Action<IHostContainer>, IHostContainer> RootFactory; // 注入根构建

        public readonly struct ParentOverride : IDisposable
        {
            public ParentOverride(Scope p) { lock (Sync) GlobalParents.Push(p); }
            public void Dispose() { lock (Sync) GlobalParents.Pop(); }
        }
        public static ParentOverride EnqueueParent(Scope p) => new ParentOverride(p);

        // ---- 实例字段 ----
        public IHostContainer Container { get; private set; }
        public Scope Parent { get; set; }
        public Type ParentType;            // 等价 parentReference.Type
        public Scope ParentObject;         // 等价 parentReference.Object
        public bool IsRoot;
        public bool AutoRun = true;
        readonly List<IInstaller> localInstallers = new();
        readonly List<Scope> registry; // 模拟"场景中所有 scope"用于按类型查找

        public Scope(List<Scope> sceneRegistry) { registry = sceneRegistry; registry.Add(this); }

        public void AddInstaller(IInstaller i) => localInstallers.Add(i);

        // ---- 生命周期入口（模拟 Awake）----
        public void OnAwake()
        {
            try { if (AutoRun) Build(); }
            catch (ParentTypeNotFound) when (!IsRoot)
            {
                if (WaitingList.Contains(this)) throw;
                WaitingList.Add(this);                       // 父未就绪 → 入队
            }
        }

        public void Build()
        {
            Parent ??= GetRuntimeParent();
            if (Parent != null)
            {
                if (Parent.IsRoot && Parent.Container == null) Parent.Build();
                Container = Parent.Container.CreateScope(InstallTo, SetContainer);
            }
            else
            {
                Container = RootFactory(InstallTo, SetContainer);   // 根：新建
            }
            AwakeWaitingChildren(this);                      // 唤醒等待我的子
        }

        void SetContainer(IHostContainer c) { Container = c; /* AutoInjectAll(); */ }

        void InstallTo(IBuilder b)
        {
            Configure(b);
            foreach (var i in localInstallers) i.Install(b);
            localInstallers.Clear();
            lock (Sync) foreach (var i in GlobalInstallers) i.Install(b);
            b.RegisterInstance(this);                         // 自注册 LifetimeScope
            b.OnBuilt(SetContainer);
        }

        protected virtual void Configure(IBuilder b) { }

        Scope GetRuntimeParent()
        {
            if (IsRoot) return null;
            if (ParentObject != null) return ParentObject;            // ② 显式引用
            if (ParentType != null && ParentType != GetType())       // ④ 类型查找
            {
                var found = registry.Find(s => ParentType.IsInstanceOfType(s) && s.Container != null);
                if (found != null) return found;
                throw new ParentTypeNotFound($"parent of type {ParentType} not found");
            }
            lock (Sync) if (GlobalParents.Count > 0) return GlobalParents.Peek(); // ⑤ 全局栈
            return Root;                                              // ⑥ 全局根
        }

        public static Scope Root;   // 等价 VContainerSettings 根

        // ---- 等待队列重试 ----
        static void AwakeWaitingChildren(Scope awakenParent)
        {
            if (WaitingList.Count == 0) return;
            var buffer = new List<Scope>();
            for (var i = WaitingList.Count - 1; i >= 0; i--)
            {
                var w = WaitingList[i];
                if (w.ParentType != null && w.ParentType.IsInstanceOfType(awakenParent))
                { w.ParentObject = awakenParent; WaitingList.RemoveAt(i); buffer.Add(w); }
            }
            foreach (var w in buffer) w.OnAwake();   // 重试
        }

        public void OnDestroy() { Container?.Dispose(); Container = null; WaitingList.Remove(this); }

        public static void SetRootFactory(Func<Action<IBuilder>, Action<IHostContainer>, IHostContainer> f) => RootFactory = f;
    }
}
```

## 使用示例

```csharp
var scene = new List<Scope>();
Scope.SetRootFactory((install, onBuilt) => { /* new ContainerBuilder; install(b); var c = b.Build(); onBuilt(c); return c; */ return null; });

// 子作用域先于父 Awake 的竞态：
var child = new Scope(scene) { ParentType = typeof(GameScope) };
child.OnAwake();          // 父未就绪 → 进入 WaitingList，不崩溃

var parent = new GameScope(scene) { IsRoot = false };
parent.OnAwake();         // 父建好 → AwakeWaitingChildren 自动唤醒 child 重试
```

## 取舍自检

- ✅ **保留**：父定位优先级链、父未就绪的等待队列 + 异常重试 + 唤醒、回调式 `SetContainer` 回填、`InstallTo` 安装器汇聚 + 自注册、全局栈的 `using` 压栈。
- ❌ **砍掉**：MonoBehaviour/执行顺序/场景事件、`ParentReference` 序列化、`VContainerSettings` preloaded 单例、prefab 实例化注入、EntryPoint 调度（M6）。
- ⚠️ **最容易搞错**：等待队列的"重入保护"。`OnAwake` 捕获异常后必须先判 `WaitingList.Contains(this)`：若已在等待表还找不到父，应**重新抛出**而非再次入队，否则同一 scope 会反复 Awake-失败-入队形成死循环。原版正是 `if (WaitingList.Contains(this)) throw;`。
