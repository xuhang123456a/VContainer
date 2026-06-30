# M5 Unity 生命周期作用域 · 考题

## 🟢 概念题

1. `LifetimeScope` 用什么机制保证它的 Awake 早于普通被注入脚本？
2. `LifetimeScope.Build` 为什么用 `RegisterBuildCallback(SetContainer)` 而不是 `Container = builder.Build()`？
3. `IInstaller` 与 `ActionInstaller` 的关系是什么？
4. `ParentReference` 序列化时存的是 Type 还是字符串？为什么？
5. Root 作用域和普通作用域在销毁时机上有何不同？

<details><summary>参考答案要点</summary>

1. `[DefaultExecutionOrder(-5000)]`，让 LifetimeScope 在 Unity 脚本执行顺序中整体提前。
2. 因为构建流程（CreateScope/EmitCallbacks）需要在"容器实例已存在、对外引用未暴露"时就回填 Container 字段，并与"建好后立即处理入口点"的回调链统一顺序；返回值赋值无法满足回调内即用 Container 的需求。
3. `ActionInstaller` 是 `IInstaller` 的适配器，把 `Action<IContainerBuilder>` 包成安装器，并提供隐式转换。
4. 存字符串 `TypeName`。`Type` 不可直接序列化，反序列化时遍历 AppDomain 程序集 `GetType(TypeName)` 还原。
5. Root 由 `VContainerSettings` 实例化并 `DontDestroyOnLoad`，跨场景存活；普通作用域随其 GameObject 的 OnDestroy 释放容器。
</details>

## 🟡 机制题

1. `GetRuntimeParent` 的完整优先级链有哪几级？
2. 当子作用域 Awake 时父类型引用找不到，会发生什么？后续如何被救活？
3. `InstallTo` 依次做了哪些事？为什么最后要自动 `RegisterInstance<LifetimeScope>(this)`？
4. `GetOrCreateRootLifetimeScopeInstance` 为什么先 `SetActive(false)` 再 Instantiate？
5. `ParentOverrideScope`/`ExtraInstallationScope` 用 `readonly struct` + `using` 实现了什么 Helper 语义？
6. `AwakeWaitingChildren` 和 `OnSceneLoaded` 两个唤醒入口分别在什么时机触发？

<details><summary>参考答案要点</summary>

1. ① IsRoot→null；② parentReference.Object；③ FindParent() 重写；④ parentReference.Type 场景查找（失败抛异常）；⑤ GlobalOverrideParents 栈顶；⑥ VContainerSettings 根。
2. 抛 `VContainerParentTypeReferenceNotFound`，被 `Awake` 的 `catch when(!IsRoot)` 捕获 → `EnqueueAwake` 入 `WaitingList`。父 Awake 完成时 `AwakeWaitingChildren` 按类型匹配唤醒，或 `OnSceneLoaded` 按场景匹配重试。
3. Configure(子类重写) → localExtraInstallers → 全局 GlobalExtraInstallers → RegisterInstance<LifetimeScope>(this).AsSelf() → EnsureDispatcherRegistered。自注册是为了让服务能注入"自己所在的作用域"（如用它 CreateChild/Instantiate）。
4. 防止 Root prefab 内部的子脚本/子 LifetimeScope 在根容器建好前就 Awake 被注入（拿到空容器）；先禁用整体、实例化后再启用。
5. RAII 式作用域：构造 Push、Dispose Pop（lock 保护）。在 `using` 块内创建的作用域会临时采用栈顶父 / 额外安装器，离开块自动还原。
6. `AwakeWaitingChildren` 在某个父 `Build` 完成末尾触发（按 ParentType 匹配）；`OnSceneLoaded` 在场景加载完成事件触发（按 gameObject.scene 匹配），是兜底重试。
</details>

## 🔴 架构陷阱题

1. **时序竞态**：为什么"子被注入脚本可能早于父 LifetimeScope Awake"这个问题需要等待队列？只靠 `[DefaultExecutionOrder]` 不够吗？
2. `Awake` 的 `catch` 里若不写 `if (WaitingList.Contains(this)) throw;` 会出现什么后果？
3. 父作用域 Dispose 时，它的子作用域会被自动 Dispose 吗？（联系 M4）这对 LifetimeScope 意味着什么责任？
4. 把根作用域实例化时漏掉 `DontDestroyOnLoad` 会有什么表现？
5. 两个不同 LifetimeScope 都把同一个全局服务声明为 Singleton，分别在各自作用域解析，结果是同一对象吗？（联系 M4 单例归属）
6. `parentReference.Type` 反序列化时遍历所有程序集 `GetType`，这在什么情况下会失败？失败的运行期表现是什么？
7. `EnsureDispatcherRegistered` 用 `containerBuilder.Exists(typeof(EntryPointDispatcher))` 做幂等守卫，如果去掉会怎样？

<details><summary>参考答案要点</summary>

1. `[DefaultExecutionOrder]` 只调整 LifetimeScope 之间/与普通脚本的相对顺序，但跨场景/动态实例化/嵌套 prefab 等情况下，"子作用域所在对象先 Awake 而父尚未 Awake"仍可能发生。等待队列 + 重试是对这种无法靠静态顺序解决的动态时序的兜底。
2. 同一 scope 反复"Awake→找不到父→入队→被唤醒重试→仍找不到→再入队"，可能无限循环或重复入队膨胀。该守卫确保"已在等待还失败"时直接抛错暴露真问题。
3. 不会自动级联（M4 父 Dispose 不级联子）。所以每个 LifetimeScope 必须在自己 OnDestroy 时 DisposeCore 自己的容器——Unity 的对象销毁顺序帮忙触发，但责任在各 scope 自身。
4. 切换场景时根作用域随旧场景被销毁，其容器 Dispose，全局单例失效；新场景里依赖根的子作用域会找不到父或拿到已释放容器。
5. 不是。各自作用域 registry 都 `Exists` 该单例 → 各自走"本层单例"分支，在各自作用域建一个。要全局唯一应在根/父唯一处注册，子只解析不重注册。
6. 类型被重命名/移动程序集/被 strip（IL2CPP 裁剪）/拼写错误时 `GetType(TypeName)` 全返回 null，`Type` 保持 null。运行期表现为父定位走不到类型查找分支或查找抛 NotFound，作用域可能错挂到全局根或一直等待。
7. 重复注册 `EntryPointDispatcher` 和异常处理器、重复注册"建好后 Dispatch"的回调，导致入口点（Initialize/Start/Tick）被重复执行。幂等守卫保证一个容器只调度一次。
</details>

## ✍️ 实操题

1. 用 Facade 复现"子先于父 Awake"竞态测试：先 `child.OnAwake()`（父未就绪进等待队列），再 `parent.OnAwake()`，断言 child 最终拿到了 Container。
2. 给 Facade 增加 `ExtraInstallation`（using 作用域），使块内创建的所有 Scope 都额外执行某安装器，对照 `ExtraInstallationScope`。
3. 实现 `CreateChild`：新建子 Scope、设其 `ParentObject = this`、立即 `OnAwake()`，并验证子的 Container 父链指向当前 Scope 的 Container。

<details><summary>参考答案要点</summary>

1. `child.OnAwake()` 后断言 `WaitingList.Contains(child)` 且 `child.Container==null`；`parent.OnAwake()` 触发 `AwakeWaitingChildren` 唤醒 child；最终断言 `child.Container!=null` 且 `WaitingList` 不含 child。
2. 加 `ExtraInstallationScope` struct：构造 `GlobalInstallers.Push(installer)`、Dispose `Pop`。`InstallTo` 已遍历 `GlobalInstallers`，故块内新建 scope 自动带上。
3. `CreateChild` 内 `var c = new ChildScope(registry){ ParentObject = this }; c.OnAwake();`。验证：子的 `Build` 走 `Parent.Container.CreateScope`，故子容器 parent 链 == 当前 Container。
</details>
