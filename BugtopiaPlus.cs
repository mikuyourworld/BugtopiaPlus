using BepInEx;
using BepInEx.Configuration; // 引入配置系统命名空间
using BepInEx.Logging;
using HarmonyLib;
using Peecub; // 游戏原本的命名空间
using TMPro; // TMP 文本组件命名空间

namespace BugtopiaPlus
{
    [BepInPlugin("BugtopiaPlus", "BugtopiaPlus", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Plugin Instance; // 单例实例，方便访问配置

        // --- 定义配置项 (Config Entries) ---
        public static ConfigEntry<bool> EnableUnrestrictedFeeding;
        public static ConfigEntry<bool> EnableAutoTransfer;
        public static ConfigEntry<int> TargetHabitatBoxIndex;
        public static ConfigEntry<int> MaxHabitatBoxCapacity;

        private void Awake()
        {
            Log = base.Logger;
            Instance = this;

            // --- 绑定配置文件 ---
            // Bind(分组名称, 配置项键名, 默认值, 描述)

            EnableUnrestrictedFeeding = Config.Bind("Toggles", 
                "EnableUnrestrictedFeeding", 
                true, 
                "为true时，你可以在虫虫为蛹阶段时喂食。");

            EnableAutoTransfer = Config.Bind("Toggles",
                "EnableAutoTransfer",
                true,
                "为true时，繁育箱孵化出的幼虫将自动转移到指定的栖息地箱子。");

            TargetHabitatBoxIndex = Config.Bind("Settings",
                "TargetHabitatBoxIndex",
                0, // 默认为第一个箱子
                "设置自动转移目标栖息地箱子的索引（0表示第一个箱子，依次类推）。");

            MaxHabitatBoxCapacity = Config.Bind("Settings",
                "MaxHabitatBoxCapacity",
                40, // 默认为游戏原本满级的 40
                "设置最大级别生态箱的容量上限（默认为满级40，可自由上调或下调）。");

            // 应用补丁
            Harmony.CreateAndPatchAll(typeof(UnifiedPatches));
            Harmony.CreateAndPatchAll(typeof(LandscapeScalePatch));
            Harmony.CreateAndPatchAll(typeof(UISpecialModalPanelPatch));
            Harmony.CreateAndPatchAll(typeof(MaxCapacityPatch));
            Log.LogInfo("[AutoTransfer] BugtopiaPlus loaded successfully with Config support!");
        }

        private void Update()
        {
            // 在每一帧检查是否需要刷新皇冠标识
            CrownManager.OnUpdate();
        }
    }

    // 将所有补丁整合到一个类中，显得更整洁
    public static class UnifiedPatches
    {
        [HarmonyPatch(typeof(Peecub.UIModalPanel), "Close")]
        [HarmonyPostfix]
        public static void UIModalPanel_Close_Postfix()
        {
            if (Plugin.EnableAutoTransfer.Value && Plugin.Instance != null)
            {
                Plugin.Instance.StartCoroutine(CheckOfflineHatchesDelayed());
            }
        }

        private static System.Collections.IEnumerator CheckOfflineHatchesDelayed()
        {
            // 等待半秒钟让关闭动画完成
            yield return new UnityEngine.WaitForSeconds(0.5f);
            Plugin.Log.LogInfo("[AutoTransfer] Delay finished. Scanning MateBoxes.");

            if (DataManager.instance == null || DataManager.instance.mateBoxController == null || DataManager.instance.boxes == null)
            {
                Plugin.Log.LogWarning("[AutoTransfer] DataManager components are null. Aborting.");
                yield break;
            }

            int targetIndex = Plugin.TargetHabitatBoxIndex.Value;
            if (targetIndex < 0 || targetIndex >= DataManager.instance.boxes.Count)
            {
                Plugin.Log.LogWarning($"[AutoTransfer] Target index {targetIndex} is invalid. Aborting.");
                yield break;
            }

            var targetBox = DataManager.instance.boxes[targetIndex];
            if (targetBox == null || targetBox.transform == null)
            {
                Plugin.Log.LogWarning("[AutoTransfer] Target box is null. Aborting.");
                yield break;
            }

            foreach (var mateBox in DataManager.instance.mateBoxController.mateBoxes)
            {
                if (mateBox == null || mateBox.transform == null) continue;

                var mateBoxTraverse = Traverse.Create(mateBox);
                var firstIdle = mateBoxTraverse.Property("FirstIdle").GetValue<IdleObject>() ?? mateBoxTraverse.Field("firstIdle").GetValue<IdleObject>();
                var secondIdle = mateBoxTraverse.Property("SecondIdle").GetValue<IdleObject>() ?? mateBoxTraverse.Field("secondIdle").GetValue<IdleObject>();
                var eggIdle = mateBoxTraverse.Property("EggIdle").GetValue<IdleObject>() ?? mateBoxTraverse.Field("eggIdle").GetValue<IdleObject>();

                Plugin.Log.LogInfo($"[AutoTransfer] Scanning MateBox {mateBox.name}. Children count: {mateBox.transform.childCount}. FirstIdle={(firstIdle != null ? firstIdle.GetBioName() : "null")}, SecondIdle={(secondIdle != null ? secondIdle.GetBioName() : "null")}, EggIdle={(eggIdle != null ? eggIdle.GetBioName() : "null")}");

                var validBabies = new System.Collections.Generic.List<IdleObject>();
                var corruptedBabies = new System.Collections.Generic.List<IdleObject>();

                // 第一步：安全收集所有子节点，防止枚举过程中修改 Transform 导致 InvalidOperationException
                foreach (UnityEngine.Transform child in mateBox.transform)
                {
                    var bug = child.GetComponent<IdleObject>();
                    if (bug != null && bug != firstIdle && bug != secondIdle)
                    {
                        // 正常的繁育箱应该最多只有一个未长大的蛋或幼虫
                        if (validBabies.Count == 0 && (int)bug.growthStage >= 0)
                        {
                            validBabies.Add(bug);
                        }
                        else
                        {
                            // 发现多余的（由之前无限生成Bug导致的复制体），将其视为脏数据直接清理
                            corruptedBabies.Add(bug);
                        }
                    }
                }

                // 第二步：清理脏数据
                foreach (var corruptedBug in corruptedBabies)
                {
                    Plugin.Log.LogWarning($"[AutoTransfer] Destroying corrupted duplicated bug {corruptedBug.GetBioName()} from MateBox!");
                    UnityEngine.Object.Destroy(corruptedBug.gameObject);
                }

                if (corruptedBabies.Count > 0)
                {
                    // 如果存在脏数据，强制游戏更新一次 eggIdle 引用，防止空引用或指向已被销毁的对象
                    var updateEggIdleMethod = mateBoxTraverse.Method("UpdateEggIdle");
                    if (updateEggIdleMethod.MethodExists()) updateEggIdleMethod.GetValue();
                }

                // 第三步：处理合法的离线孵化虫系
                if (validBabies.Count > 0)
                {
                    var validBug = validBabies[0];
                    if ((int)validBug.growthStage > 0 && (int)validBug.growthStage < 4)
                    {
                        Plugin.Log.LogInfo($"[AutoTransfer] Found offline-hatched bug {validBug.GetBioName()}. Moving to TargetBox.");
                        
                        validBug.transform.SetParent(targetBox.transform);

                        var generatePreEggMethod = mateBoxTraverse.Method("GeneratePreEgg", mateBox);
                        if (!generatePreEggMethod.MethodExists()) 
                        {
                            generatePreEggMethod = Traverse.Create(DataManager.instance.mateBoxController).Method("GeneratePreEgg", mateBox);
                        }
                        
                        if (generatePreEggMethod.MethodExists())
                        {
                            generatePreEggMethod.GetValue();
                        }

                        var msgDispatcherType = System.Type.GetType("com.ootii.Messages.MessageDispatcher, Assembly-CSharp");
                        if (msgDispatcherType != null)
                        {
                            var sendMsgMethod = msgDispatcherType.GetMethod("SendMessage", new System.Type[] { typeof(string), typeof(float) });
                            if (sendMsgMethod != null)
                            {
                                sendMsgMethod.Invoke(null, new object[] { "OnIdleObjectMoved", 0f });
                            }
                        }
                    }
                }
            }
        }
        [HarmonyPatch(typeof(IdleObject), "TryFeed")]
        [HarmonyPrefix]
        public static bool TryFeed_Prefix(IdleObject __instance, ref bool __result)
        {
            // 如果配置文件中关闭了这个功能，则返回 true，执行游戏原本的逻辑
            if (!Plugin.EnableUnrestrictedFeeding.Value)
            {
                return true; 
            }

            // --- 以下是你原本的强制喂食逻辑 ---
            
            // 必要的空值和激活检查
            if (__instance.nextStageIdleObject == null || !__instance.isActive)
            {
                __result = false;
                return false; // 拦截原方法
            }

            // 增加经验
            __instance.exp += DataManager.instance.GetExpPerFeed();
            __result = true; // 标记喂食成功

            // 每次喂食都可能触发升级，我们可以顺便在这里告诉皇冠管理器该刷新了
            CrownManager.RequireUpdate();

            return false; // 返回 false 以拦截游戏原有的 TryFeed 方法
        }
        // ----------------------------------------------------
        // 3. 补丁：在虫子的 UI 详情面板长度旁边加上 MAX 标识
        // ----------------------------------------------------
        [HarmonyPatch(typeof(Peecub.UIIdleInfoPanel), "Init")]
        [HarmonyPostfix]
        public static void UIIdleInfoPanel_Init_Postfix(Peecub.UIIdleInfoPanel __instance, IdleObject idle)
        {
            object sizeTextObj = Traverse.Create(__instance).Field("sizeText").GetValue();
            if (sizeTextObj == null) return;
            
            UnityEngine.Component sizeTextComp = sizeTextObj as UnityEngine.Component;
            if (sizeTextComp == null) return;

            UnityEngine.Transform sizeTextTransform = sizeTextComp.transform;
            UnityEngine.Transform maxLabelTrans = sizeTextTransform.Find("BugtopiaMaxLabel");

            // 如果不是最大的，隐藏之前的副物体并跳过
            if (idle == null || !CrownManager.IsMaxBug(idle))
            {
                if (maxLabelTrans != null) maxLabelTrans.gameObject.SetActive(false);
                return;
            }

            // 检查是否同时为历史最高记录（图鉴最大）
            bool isHistorical = CrownManager.IsHistoricalMax(idle);

            UnityEngine.GameObject maxLabelObj;
            if (maxLabelTrans == null)
            {
                // 实例化一个独立的子节点，彻底断绝被排版系统挤占位置的问题
                maxLabelObj = UnityEngine.Object.Instantiate(sizeTextComp.gameObject, sizeTextTransform);
                maxLabelObj.name = "BugtopiaMaxLabel";
            }
            else
            {
                maxLabelObj = maxLabelTrans.gameObject;
            }

            maxLabelObj.SetActive(true);

            // 向右平移 90 单位，避开尺子，高度设为 0 使其与原文字底部对齐
            maxLabelObj.transform.localPosition = new UnityEngine.Vector3(90f, 0f, 0f);
            
            // 单独缩小这个物件
            maxLabelObj.transform.localScale = new UnityEngine.Vector3(0.85f, 0.85f, 1f);

            UnityEngine.Component clonedTextComp = maxLabelObj.GetComponent(sizeTextComp.GetType());
            if (clonedTextComp != null)
            {
                string colorHex = isHistorical ? "#FF4500" : "#00BFFF";
                string labelStr = isHistorical ? "MAXX" : "MAX";
                Traverse.Create(clonedTextComp).Property("text").SetValue($"<color={colorHex}><b>{labelStr}</b></color>");
            }
        }

        // ----------------------------------------------------
        // 4. 野外繁育箱自动转移刚孵化的幼虫
        // ----------------------------------------------------
        [HarmonyPatch(typeof(Peecub.LevelManager), "OnUpgradeRefresh")]
        [HarmonyPostfix]
        public static void LevelManager_OnUpgradeRefresh_Postfix(Peecub.LevelManager __instance, IdleObject oldIdleObject, IdleObject newIdleObject)
        {
            if (!Plugin.EnableAutoTransfer.Value) return;
            if (oldIdleObject == null || newIdleObject == null) return;

            // 检查老虫子是否在繁殖箱，且是否是刚从蛋孵化出来
            // GrowthStage 0 对应于“卵”
            if (oldIdleObject.IsInMateBox() && (int)oldIdleObject.growthStage == 0)
            {
                try
                {
                    int targetIndex = Plugin.TargetHabitatBoxIndex.Value;
                    if (DataManager.instance != null && DataManager.instance.boxes != null)
                    {
                        if (targetIndex >= 0 && targetIndex < DataManager.instance.boxes.Count)
                        {
                            var targetBox = DataManager.instance.boxes[targetIndex];
                            if (targetBox != null && targetBox.transform != null)
                            {
                                Plugin.Log.LogInfo($"[AutoTransfer] Moving hatched bug {newIdleObject.GetBioName()} to Box {targetIndex}");
                                
                                // 绕过 IsFull 检测，强制将新的宝宝虫转入目标箱子
                                newIdleObject.transform.SetParent(targetBox.transform);
                                
                                // 通知繁育箱这枚蛋已经离箱，必须调用 GeneratePreEgg 重新启动繁育循环
                                var mateBox = oldIdleObject.GetMateBox();
                                if (mateBox != null && DataManager.instance != null && DataManager.instance.mateBoxController != null)
                                {
                                    // 防止 RemoveIdleReference 抛出空引用异常（如果 mateBox.eggIdle 等于 null）
                                    // 遇到异常会中断 Harmony Postfix，导致游戏原逻辑 Object.Destroy(oldIdleObject) 被跳过，从而产生无限复制虫子的恶性Bug！
                                    var eggIdle = Traverse.Create(mateBox).Property("EggIdle").GetValue<IdleObject>() ?? Traverse.Create(mateBox).Field("eggIdle").GetValue<IdleObject>();
                                    if (eggIdle != null)
                                    {
                                        mateBox.RemoveIdleReference(oldIdleObject);
                                    }

                                    // 触发重新生蛋的逻辑
                                    var generatePreEggMethod = Traverse.Create(DataManager.instance.mateBoxController).Method("GeneratePreEgg", mateBox);
                                    if (generatePreEggMethod.MethodExists())
                                    {
                                        generatePreEggMethod.GetValue();
                                        Plugin.Log.LogInfo("[AutoTransfer] Triggered GeneratePreEgg to resume breeding.");
                                    }
                                }

                                // 触发游戏内部的消息系统，告知有虫子移动过了，以便 UI 或其他系统刷新状态
                                var msgDispatcherType = System.Type.GetType("com.ootii.Messages.MessageDispatcher, Assembly-CSharp");
                                if (msgDispatcherType != null)
                                {
                                    var sendMsgMethod = msgDispatcherType.GetMethod("SendMessage", new System.Type[] { typeof(string), typeof(float) });
                                    if (sendMsgMethod != null)
                                    {
                                        sendMsgMethod.Invoke(null, new object[] { "OnIdleObjectMoved", 0f });
                                    }
                                }
                            }
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"[AutoTransfer] Target box index {targetIndex} is out of bounds!");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"[AutoTransfer] CRITICAL ERROR during OnUpgradeRefresh: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }

    /// <summary>
    /// 用于管理最大虫子皇冠标识的逻辑核心类
    /// </summary>
    public static class CrownManager
    {
        // 避免一帧内多次重复计算，使用此标记位
        private static bool _needsUpdate = true; // 默认进来就刷新一次
        private static float _timeSinceLastCheck = 0f;
        
        // 存储当前各种类最大的虫子，供 UI 面板随时查询
        public static System.Collections.Generic.Dictionary<string, IdleObject> _maxBugsBySpecies 
            = new System.Collections.Generic.Dictionary<string, IdleObject>();

        public static void RequireUpdate()
        {
            _needsUpdate = true;
        }

        // 在 Plugin 的 Update() 里面调用
        public static void OnUpdate()
        {
            _timeSinceLastCheck += UnityEngine.Time.deltaTime;

            // 每隔 2 秒钟，强制检查一次有没有虫子被卖掉或者消失
            if (_timeSinceLastCheck > 2.0f)
            {
                _needsUpdate = true;
                _timeSinceLastCheck = 0f;
            }

            if (_needsUpdate)
            {
                UpdateCrowns();
                _needsUpdate = false;
            }
        }

        public static bool IsMaxBug(IdleObject bug)
        {
            if (bug == null || !bug.isActive || bug.nextStageIdleObject != null) return false;
            string speciesName = GetSpeciesIdFromBug(bug);
            return _maxBugsBySpecies.ContainsKey(speciesName) && _maxBugsBySpecies[speciesName] == bug;
        }

        public static bool IsHistoricalMax(IdleObject bug)
        {
            if (bug == null) return false;
            if (Peecub.ArchiveManager.instance == null) return false;

            var archList = Peecub.ArchiveManager.instance.GetArchivedIdleDataList();
            if (archList == null) return false;

            foreach (var data in archList)
            {
                if (data.id == bug.id)
                {
                    int colorIndex = (int)bug.colorType;
                    float histMax = 0f;
                    
                    if (data.colorDataList != null && colorIndex >= 0 && colorIndex < data.colorDataList.Count)
                    {
                        histMax = data.colorDataList[colorIndex].maxLength;
                    }
                    else if (data.colorDataList != null && data.colorDataList.Count > 0)
                    {
                        histMax = data.colorDataList[0].maxLength;
                    }

                    // 允许 0.001 的浮点数误差，如果当前虫子的大于等于历史最大值记录，就是图鉴最大
                    return bug.size >= histMax - 0.001f;
                }
            }
            return false;
        }

        public static void UpdateCrowns()
        {
            // 1. 查找场景中所有激活的 IdleObject
            IdleObject[] allBugs = UnityEngine.Object.FindObjectsOfType<IdleObject>();
            if (allBugs.Length == 0) return; // 没有虫子就不打印了，防止刷屏

            _maxBugsBySpecies.Clear();
            int matureBugCount = 0;

            foreach (var bug in allBugs)
            {
                // 只考虑成年虫子（nextStageIdleObject 为空就是最终形态）并且处于激活状态
                if (bug != null && bug.isActive && bug.nextStageIdleObject == null)
                {
                    matureBugCount++;
                    // 更严谨的获取物种名字
                    string speciesName = GetSpeciesIdFromBug(bug);

                    // 如果还没记录过这个物种，或者这只比之前记录的同类更大
                    if (!_maxBugsBySpecies.ContainsKey(speciesName) || bug.size > _maxBugsBySpecies[speciesName].size)
                    {
                        _maxBugsBySpecies[speciesName] = bug;
                    }
                }
            }

            // 暂时移除 3D 头顶 UI 文字刷新的逻辑，转而通过 UI 面板显示
        }

        private static string GetSpeciesIdFromBug(IdleObject bug)
        {
            // 处理例如 "0013(Clone)" 或者 "0013" 提取出 "0013"
            string name = bug.gameObject.name;
            int cloneIndex = name.IndexOf("(Clone)");
            string baseId = cloneIndex >= 0 ? name.Substring(0, cloneIndex).Trim() : name.Trim();
            
            // 加上 colorType 来区分亚种 (稀有度)
            return $"{baseId}_{(int)bug.colorType}";
        }
    [HarmonyPatch(typeof(Peecub.UITitle), "OnClick")]
    [HarmonyPostfix]
    public static void UITitle_OnClick_Postfix()
    {
        if (Plugin.EnableAutoTransfer.Value && Plugin.Instance != null)
        {
            Plugin.Log.LogInfo("[AutoTransfer] UITitle_OnClick hooked. Starting delayed scan logic.");
            Plugin.Instance.StartCoroutine(CheckOfflineHatchesDelayed());
        }
    }

    private static System.Collections.IEnumerator CheckOfflineHatchesDelayed()
    {
        Plugin.Log.LogInfo("[AutoTransfer] CheckOfflineHatchesDelayed started, waiting 1 second...");
        yield return new UnityEngine.WaitForSeconds(1.0f);
        Plugin.Log.LogInfo("[AutoTransfer] Delay finished. Scanning MateBoxes.");

        if (DataManager.instance == null || DataManager.instance.mateBoxController == null || DataManager.instance.boxes == null)
        {
            Plugin.Log.LogWarning("[AutoTransfer] DataManager components are null. Aborting.");
            yield break;
        }

        int targetIndex = Plugin.TargetHabitatBoxIndex.Value;
        if (targetIndex < 0 || targetIndex >= DataManager.instance.boxes.Count)
        {
            Plugin.Log.LogWarning($"[AutoTransfer] Target index {targetIndex} is invalid. Aborting.");
            yield break;
        }

        var targetBox = DataManager.instance.boxes[targetIndex];
        if (targetBox == null || targetBox.transform == null)
        {
            Plugin.Log.LogWarning("[AutoTransfer] Target box is null. Aborting.");
            yield break;
        }

        foreach (var mateBox in DataManager.instance.mateBoxController.mateBoxes)
        {
            if (mateBox == null || mateBox.transform == null) continue;

            var mateBoxTraverse = Traverse.Create(mateBox);
            var firstIdle = mateBoxTraverse.Property("FirstIdle").GetValue<IdleObject>() ?? mateBoxTraverse.Field("firstIdle").GetValue<IdleObject>();
            var secondIdle = mateBoxTraverse.Property("SecondIdle").GetValue<IdleObject>() ?? mateBoxTraverse.Field("secondIdle").GetValue<IdleObject>();
            var eggIdle = mateBoxTraverse.Property("EggIdle").GetValue<IdleObject>() ?? mateBoxTraverse.Field("eggIdle").GetValue<IdleObject>();

            Plugin.Log.LogInfo($"[AutoTransfer] Scanning MateBox {mateBox.name}. Children count: {mateBox.transform.childCount}. FirstIdle={(firstIdle != null ? firstIdle.GetBioName() : "null")}, SecondIdle={(secondIdle != null ? secondIdle.GetBioName() : "null")}, EggIdle={(eggIdle != null ? eggIdle.GetBioName() : "null")}");

            foreach (UnityEngine.Transform child in mateBox.transform)
            {
                var bug = child.GetComponent<IdleObject>();
                if (bug != null)
                {
                    Plugin.Log.LogInfo($"[AutoTransfer] Found child: {bug.GetBioName()} - Stage: {bug.growthStage} - IsFirst: {bug == firstIdle} - IsSecond: {bug == secondIdle}");
                    
                    if (bug != firstIdle && bug != secondIdle)
                    {
                        if ((int)bug.growthStage > 0 && (int)bug.growthStage < 4)
                        {
                            Plugin.Log.LogInfo($"[AutoTransfer] ^^^ THIS BUG WILL BE TRANSFERRED ^^^");
                            
                            mateBox.RemoveIdleReference(bug);
                            bug.transform.SetParent(targetBox.transform);

                            var generatePreEggMethod = mateBoxTraverse.Method("GeneratePreEgg", mateBox);
                            if (!generatePreEggMethod.MethodExists()) 
                            {
                                generatePreEggMethod = Traverse.Create(DataManager.instance.mateBoxController).Method("GeneratePreEgg", mateBox);
                            }
                            
                            if (generatePreEggMethod.MethodExists())
                            {
                                generatePreEggMethod.GetValue();
                                Plugin.Log.LogInfo("[AutoTransfer] Triggered GeneratePreEgg for offline hatch.");
                            }

                            var msgDispatcherType = System.Type.GetType("com.ootii.Messages.MessageDispatcher, Assembly-CSharp");
                            if (msgDispatcherType != null)
                            {
                                var sendMsgMethod = msgDispatcherType.GetMethod("SendMessage", new System.Type[] { typeof(string), typeof(float) });
                                if (sendMsgMethod != null)
                                {
                                    sendMsgMethod.Invoke(null, new object[] { "OnIdleObjectMoved", 0f });
                                }
                            }
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogInfo($"[AutoTransfer] Found child WITHOUT IdleObject: {child.name}");
                }
            }
        }
        Plugin.Log.LogInfo("[AutoTransfer] Scan complete.");
    }
}

    [HarmonyPatch(typeof(Peecub.UIPositionTools))]
    public class LandscapeScalePatch
    {
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update(Peecub.UIPositionTools __instance)
        {
            if (__instance.target2DObject != null)
            {
                float scroll = UnityEngine.Input.mouseScrollDelta.y;
                bool shrink = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.LeftBracket) || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Minus);
                bool enlarge = UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.RightBracket) || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Equals);
                bool resetScale = UnityEngine.Input.GetMouseButtonDown(2) || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.R); // 中键或R键还原默认缩放

                if (resetScale)
                {
                    UnityEngine.Vector3 currentScale = __instance.target2DObject.localScale;
                    float signX = UnityEngine.Mathf.Sign(currentScale.x);
                    float signY = UnityEngine.Mathf.Sign(currentScale.y);
                    float signZ = UnityEngine.Mathf.Sign(currentScale.z);
                    __instance.target2DObject.localScale = new UnityEngine.Vector3(1f * signX, 1f * signY, 1f * signZ);
                    
                    if (Peecub.DataManager.instance != null)
                        Peecub.DataManager.instance.isLandscapeEditSaved = false;
                    return;
                }

                float scaleFactor = 1f;
                if (scroll > 0f || enlarge) scaleFactor = 1.05f;
                else if (scroll < 0f || shrink) scaleFactor = 0.95f;

                if (scaleFactor != 1f)
                {
                    UnityEngine.Vector3 currentScale = __instance.target2DObject.localScale;
                    
                    float signX = UnityEngine.Mathf.Sign(currentScale.x);
                    float signY = UnityEngine.Mathf.Sign(currentScale.y);
                    float signZ = UnityEngine.Mathf.Sign(currentScale.z);

                    float absX = UnityEngine.Mathf.Abs(currentScale.x) * scaleFactor;
                    float absY = UnityEngine.Mathf.Abs(currentScale.y) * scaleFactor;
                    float absZ = UnityEngine.Mathf.Abs(currentScale.z) * scaleFactor;

                    absX = UnityEngine.Mathf.Clamp(absX, 0.2f, 10f);
                    absY = UnityEngine.Mathf.Clamp(absY, 0.2f, 10f);
                    absZ = UnityEngine.Mathf.Clamp(absZ, 0.2f, 10f);

                    __instance.target2DObject.localScale = new UnityEngine.Vector3(absX * signX, absY * signY, absZ * signZ);
                    
                    if (Peecub.DataManager.instance != null)
                    {
                        Peecub.DataManager.instance.isLandscapeEditSaved = true; // 设置为 true 避免隐藏“不保存”按钮
                        // 游戏原版若 isLandscapeEditSaved 为 false，则弹出的框只会有一个确认保存按钮，隐藏不保存按钮。
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Peecub.UISpecialModalPanel))]
    public class UISpecialModalPanelPatch
    {
        [HarmonyPatch("Initialize")]
        [HarmonyPrefix]
        public static void Initialize(string message, ref bool showCancelButton)
        {
            // 如果是退出造景的弹窗，强制显示“不保存”按钮，无视原版的隐藏逻辑
            if (message != null && (message.Contains("UIModalPanel_Landscape") || message.Contains("造景")))
            {
                showCancelButton = true;
            }
        }
    }

    // --- 可配置大箱子容量限制补丁 ---
    [HarmonyPatch]
    public static class MaxCapacityPatch
    {
        // 拦截 BoxData.IsFull 方法
        [HarmonyPatch(typeof(Peecub.BoxData), "IsFull")]
        [HarmonyPrefix]
        public static bool Prefix_IsFull(Peecub.BoxData __instance, ref bool __result)
        {
            if (__instance.capacity >= 40)
            {
                // 如果是满级箱子（游戏原定40），应用 Config 里的容量
                __result = __instance.GetCount() >= Plugin.MaxHabitatBoxCapacity.Value;
                return false; // 拦截原方法
            }
            return true; // 保持原方法
        }

        // 拦截 BoxData.IsFullByCard 方法
        [HarmonyPatch(typeof(Peecub.BoxData), "IsFullByCard")]
        [HarmonyPrefix]
        public static bool Prefix_IsFullByCard(Peecub.BoxData __instance, ref bool __result)
        {
            if (__instance.capacity >= 40)
            {
                int totalCount = __instance.GetCount() + Peecub.RM.instance.cardInsectCount;
                __result = totalCount > Plugin.MaxHabitatBoxCapacity.Value;
                return false; // 拦截原方法
            }
            return true; // 保持原方法
        }

        // 拦截 UIHud.UpdateCapacity 方法，修正 UI 上的文字显示
        [HarmonyPatch(typeof(Peecub.UIHud), "UpdateCapacity")]
        [HarmonyPostfix]
        public static void Postfix_UpdateCapacity(Peecub.UIHud __instance)
        {
            if (Peecub.DataManager.instance == null) return;
            Peecub.BoxData activeBox = Peecub.DataManager.instance.GetActiveBox();
            if (activeBox != null && activeBox.capacity >= 40)
            {
                // 用反射获取私有字段 capacityText
                var capacityText = Traverse.Create(__instance).Field("capacityText").GetValue<TMPro.TextMeshProUGUI>();
                if (capacityText != null)
                {
                    int currentCount = activeBox.GetCount();
                    capacityText.text = currentCount.ToString() + "/" + Plugin.MaxHabitatBoxCapacity.Value.ToString();
                }
            }
        }
    }
}
