using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimSaveShare
{
    /// <summary>
    /// 在 FejdStartup.SetupGui 之后注入全部菜单 UI：
    ///  1. “开始游戏”面板 TabHandler 克隆出第三个页签「共享存档」（列表 + 添加共享/移除共享/开始/返回）。
    ///  2. 世界列表页的「返回」按钮下方克隆一个「共享存档」按钮，用于把选中的世界上传到 GitHub。
    /// 全程克隆原版控件，保持视觉与输入风格一致；任何异常只记日志，不影响原版菜单。
    /// </summary>
    [HarmonyPatch(typeof(FejdStartup), "SetupGui")]
    internal static class FejdStartupSetupGuiPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                SaveShareUI.Build();
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogError("SaveShare UI build failed: " + e);
            }
        }
    }

    internal static class SaveShareUI
    {
        private static FejdStartup _builtFor; // 已挂过 UI 的 FejdStartup 实例（退世界回主菜单会重建实例）
        private static TabHandler _tabs;
        private static int _ourTabIndex = -1;
        private static RectTransform _listRoot;
        private static GameObject _rowTemplate;
        private static float _rowStep = 28f;
        private static float _baseHeight = 220f;

        private static readonly List<GameObject> Rows = new List<GameObject>();
        private static readonly List<RowEntry> Entries = new List<RowEntry>();
        private static int _selected = -1;

        private class RowEntry
        {
            public SharedSaveEntry Registry;
            public World Local;
            public string Name => Registry?.Name;
        }

        public static void Build()
        {
            var fejd = FejdStartup.instance;
            if (fejd == null || fejd.m_startGamePanel == null || fejd.m_worldListRoot == null || fejd.m_worldListElement == null)
            {
                return;
            }
            if (_builtFor == fejd)
            {
                return; // 当前实例已构建过
            }

            var th = fejd.m_startGamePanel.transform.GetChild(0).GetComponent<TabHandler>();
            if (th == null || th.m_tabs == null || th.m_tabs.Count == 0 || th.m_tabs[0].m_page == null || th.m_tabs[0].m_button == null)
            {
                SaveSharePlugin.Log.LogWarning("SaveShare: start panel TabHandler not found, UI skipped");
                return;
            }
            _builtFor = fejd;
            ResetCachedState();
            _rowStep = Mathf.Max(8f, fejd.m_worldListElementStep);
            _rowTemplate = fejd.m_worldListElement;

            TabHandler.Tab tab0 = th.m_tabs[0];

            // ---- 克隆页签按钮 ----
            GameObject tabBtnClone = UnityEngine.Object.Instantiate(tab0.m_button.gameObject, tab0.m_button.transform.parent);
            tabBtnClone.name = "SaveShareTabButton";
            Button tabBtn = tabBtnClone.GetComponent<Button>();
            if (tabBtn != null)
            {
                tabBtn.onClick = new Button.ButtonClickedEvent();
            }
            SetLabel(tabBtnClone, SaveShareService.L("共享存档", "Shared Saves"));
            var tabRt = tabBtnClone.transform as RectTransform;
            var stripLayout = tab0.m_button.transform.parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (stripLayout == null && tabRt != null)
            {
                var lastTabRt = th.m_tabs[th.m_tabs.Count - 1].m_button.transform as RectTransform;
                if (lastTabRt != null)
                {
                    tabRt.anchoredPosition = lastTabRt.anchoredPosition + new Vector2(lastTabRt.rect.width + 4f, 0f);
                }
            }

            // ---- 克隆世界列表页作为「共享存档」页 ----
            GameObject pageClone = UnityEngine.Object.Instantiate(tab0.m_page.gameObject, tab0.m_page.parent);
            pageClone.name = "SaveSharePage";
            pageClone.SetActive(false);

            if (fejd.m_worldSourceInfoPanel != null)
            {
                var infoClone = FindChild(pageClone.transform, fejd.m_worldSourceInfoPanel.name);
                if (infoClone != null)
                {
                    infoClone.gameObject.SetActive(false);
                }
            }
            if (fejd.m_removeWorldDialog != null)
            {
                var dlgClone = FindChild(pageClone.transform, fejd.m_removeWorldDialog.name);
                if (dlgClone != null)
                {
                    dlgClone.gameObject.SetActive(false);
                }
            }

            // ---- 按持久化回调识别并重接各按钮 ----
            foreach (var b in pageClone.GetComponentsInChildren<Button>(true))
            {
                if (IsRowTemplate(b))
                {
                    continue; // 世界行模板保留给我们自己用
                }
                string methods = PersistentMethods(b);
                b.onClick = new Button.ButtonClickedEvent();
                if (methods.Contains("OnWorldStart"))
                {
                    b.onClick.AddListener(OnStartClicked);
                }
                else if (methods.Contains("OnStartGameBack"))
                {
                    b.onClick.AddListener(() =>
                    {
                        try
                        {
                            fejd.OnStartGameBack();
                        }
                        catch (Exception e)
                        {
                            SaveSharePlugin.Log.LogError("back failed: " + e);
                        }
                    });
                }
                else if (methods.Contains("OnWorldNew"))
                {
                    SetLabel(b.gameObject, SaveShareService.L("添加共享", "Add Shared"));
                    b.onClick.AddListener(OnAddClicked);
                }
                else if (methods.Contains("OnWorldRemove"))
                {
                    SetLabel(b.gameObject, SaveShareService.L("移除共享", "Remove Shared"));
                    b.onClick.AddListener(OnRemoveClicked);
                }
                else
                {
                    b.gameObject.SetActive(false); // 服务器选项等与共享无关的按钮
                }
            }

            // ---- 找到列表根，清掉克隆出来的世界行（保留行模板） ----
            var rootClone = FindChild(pageClone.transform, fejd.m_worldListRoot.name) as RectTransform;
            if (rootClone == null)
            {
                SaveSharePlugin.Log.LogError("SaveShare: world list root not found in cloned page, UI skipped");
                UnityEngine.Object.Destroy(pageClone);
                UnityEngine.Object.Destroy(tabBtnClone);
                _builtFor = null;
                return;
            }
            _baseHeight = Mathf.Max(120f, rootClone.rect.height);
            for (int i = rootClone.childCount - 1; i >= 0; i--)
            {
                var child = rootClone.GetChild(i);
                if (_rowTemplate != null && child.name == _rowTemplate.name)
                {
                    child.gameObject.SetActive(false);
                    continue;
                }
                UnityEngine.Object.Destroy(child.gameObject);
            }

            // ---- 注册第三个页签 ----
            _tabs = th;
            _listRoot = rootClone;
            th.m_tabs.Add(new TabHandler.Tab
            {
                m_button = tabBtn,
                m_page = pageClone.transform as RectTransform,
                m_default = false
            });
            _ourTabIndex = th.m_tabs.Count - 1;
            try
            {
                th.ActiveTabChanged += OnActiveTabChanged;
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("ActiveTabChanged subscribe failed: " + e.Message);
            }
            th.Init(false);

            // ---- 世界列表页「返回」下方加「共享存档」上传按钮 ----
            CreateUploadButton(fejd, tab0);

            RebuildList();
            SaveSharePlugin.Log.LogInfo($"SaveShare UI built (tab index {_ourTabIndex})");
        }

        public static void Refresh()
        {
            if (_builtFor != null)
            {
                RebuildList();
            }
        }

        /// <summary>进过世界再退回主菜单时 FejdStartup 会重建（旧 UI 对象已随场景销毁），重挂前清掉旧实例缓存。</summary>
        private static void ResetCachedState()
        {
            _tabs = null;
            _ourTabIndex = -1;
            _listRoot = null;
            _rowTemplate = null;
            Rows.Clear();
            Entries.Clear();
            _selected = -1;
        }

        // ============================ 事件 ============================

        private static void OnActiveTabChanged(int index)
        {
            if (index == _ourTabIndex)
            {
                RebuildList();
            }
        }

        private static void OnRowClicked(int index)
        {
            _selected = index;
            UpdateHighlights();
        }

        private static void OnStartClicked()
        {
            var entry = SelectedEntry();
            if (entry == null)
            {
                Warn(SaveShareService.L("请先在列表中选择一个共享存档。", "Select a shared save in the list first."));
                return;
            }
            if (entry.Local == null)
            {
                Warn(SaveShareService.L(
                    "该共享存档还没有下载到本地（列表中显示“未下载”）。\n请通过「添加共享」输入 GitHub 链接下载。",
                    "This shared save is not downloaded yet (shows \"Not downloaded\").\nUse Add Shared and paste its GitHub URL to download."));
                return;
            }
            var fejd = FejdStartup.instance;
            if (fejd == null)
            {
                return;
            }
            AccessTools.Field(typeof(FejdStartup), "m_world").SetValue(fejd, entry.Local);
            fejd.OnWorldStart();
        }

        private static void OnAddClicked()
        {
            UnifiedPopup.Push(new TextEntryPopup(
                SaveShareService.L("添加共享存档", "Add shared save"),
                SaveShareService.L("输入 GitHub 分享链接（仓库名下存档的主页或世界文件夹链接均可）：",
                    "Paste a GitHub share link (repo home or a specific world folder):"),
                "https://github.com/user/repo/tree/main/worlds/MyWorld",
                UnifiedPopup.Pop,
                _ => true,
                url =>
                {
                    UnifiedPopup.Pop();
                    SaveShareService.BeginDownload(url);
                },
                localizeText: false));
            UnifiedPopup.SetFocus();
        }

        private static void OnRemoveClicked()
        {
            var entry = SelectedEntry();
            if (entry == null)
            {
                Warn(SaveShareService.L("请先在列表中选择一个共享存档。", "Select a shared save in the list first."));
                return;
            }
            string name = entry.Name;
            UnifiedPopup.Push(new YesNoPopup(
                SaveShareService.L("移除共享记录", "Remove shared entry"),
                SaveShareService.L($"只从「共享存档」列表移除「{name}」的记录，不会删除本地世界文件。确定吗？",
                    $"Remove \"{name}\" from the Shared Saves list only? Local world files are kept."),
                () =>
                {
                    UnifiedPopup.Pop();
                    SaveShareService.RemoveRegistryEntry(name);
                    _selected = -1;
                    RebuildList();
                },
                UnifiedPopup.Pop, localizeText: false));
        }

        // ============================ 列表 ============================

        private static RowEntry SelectedEntry()
        {
            if (_selected < 0 || _selected >= Entries.Count)
            {
                return null;
            }
            return Entries[_selected];
        }

        private static void RebuildList()
        {
            if (_listRoot == null || _rowTemplate == null)
            {
                return;
            }
            _selected = -1;
            Entries.Clear();
            try
            {
                var worlds = SaveSystem.GetWorldList();
                foreach (var reg in SharedSaveRegistry.All)
                {
                    var e = new RowEntry { Registry = reg };
                    e.Local = worlds.FirstOrDefault(w => string.Equals(w.m_name, reg.Name, StringComparison.OrdinalIgnoreCase));
                    Entries.Add(e);
                }
            }
            catch (Exception ex)
            {
                SaveSharePlugin.Log.LogWarning("world list query failed: " + ex.Message);
            }

            foreach (var go in Rows)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            Rows.Clear();

            if (Entries.Count == 0)
            {
                var hint = UnityEngine.Object.Instantiate(_rowTemplate, _listRoot);
                hint.name = "SaveShareHintRow";
                SetRowTexts(hint,
                    SaveShareService.L("（还没有共享存档，点「添加共享」粘贴 GitHub 链接）",
                        "(No shared saves yet — click Add Shared and paste a GitHub URL)"),
                    "", false);
                var hb = hint.GetComponent<Button>();
                if (hb != null)
                {
                    hb.interactable = false;
                }
                hint.SetActive(true);
                (hint.transform as RectTransform).anchoredPosition = Vector2.zero;
                Rows.Add(hint);
            }
            else
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    var e = Entries[i];
                    var go = UnityEngine.Object.Instantiate(_rowTemplate, _listRoot);
                    go.name = "SaveShareRow" + i;
                    int idx = i;
                    var btn = go.GetComponent<Button>();
                    if (btn != null)
                    {
                        btn.onClick = new Button.ButtonClickedEvent();
                        btn.onClick.AddListener(() => OnRowClicked(idx));
                    }
                    string seedText = e.Local != null ? e.Local.m_seedName : SaveShareService.L("未下载", "Not downloaded");
                    SetRowTexts(go, e.Name ?? "?", seedText ?? "", true);
                    go.SetActive(true);
                    (go.transform as RectTransform).anchoredPosition = new Vector2(0f, -i * _rowStep);
                    Rows.Add(go);
                }
            }

            _listRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(_baseHeight, Rows.Count * _rowStep));
            UpdateHighlights();
        }

        private static void UpdateHighlights()
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                var sel = Rows[i] != null ? Rows[i].transform.Find("selected") : null;
                if (sel != null)
                {
                    sel.gameObject.SetActive(i == _selected);
                }
            }
        }

        private static void SetRowTexts(GameObject row, string name, string seed, bool keepSourcesHidden)
        {
            var nameT = row.transform.Find("name")?.GetComponent<TMP_Text>();
            if (nameT != null)
            {
                nameT.text = name ?? "";
            }
            var seedT = row.transform.Find("seed")?.GetComponent<TMP_Text>();
            if (seedT != null)
            {
                seedT.text = seed ?? "";
            }
            var modT = row.transform.Find("modifiers")?.GetComponent<TMP_Text>();
            if (modT != null)
            {
                modT.text = "";
            }
            foreach (var src in new[] { "source_cloud", "source_local", "source_legacy" })
            {
                var t = row.transform.Find(src);
                if (t != null)
                {
                    t.gameObject.SetActive(false);
                }
            }
        }

        // ============================ 工具 ============================

        /// <summary>在世界列表页「返回」按钮下方克隆一个「共享存档」上传按钮。</summary>
        private static void CreateUploadButton(FejdStartup fejd, TabHandler.Tab tab0)
        {
            try
            {
                var backBtn = FindButtonByMethod(fejd.m_startGamePanel.transform, "OnStartGameBack", tab0.m_page);
                if (backBtn == null)
                {
                    SaveSharePlugin.Log.LogWarning("SaveShare: back button (OnStartGameBack) not found, upload button skipped");
                    return;
                }
                var clone = UnityEngine.Object.Instantiate(backBtn.gameObject, backBtn.transform.parent);
                clone.name = "SaveShareUploadButton";
                var b = clone.GetComponent<Button>();
                if (b != null)
                {
                    b.onClick = new Button.ButtonClickedEvent();
                    b.onClick.AddListener(() =>
                    {
                        try
                        {
                            SaveShareService.BeginUpload();
                        }
                        catch (Exception e)
                        {
                            SaveShareService.ShowError(SaveShareService.L("共享失败", "Share failed"), e);
                        }
                    });
                }
                SetLabel(clone, SaveShareService.L("共享存档", "Share Save"));
                var orig = backBtn.transform as RectTransform;
                var rt = clone.transform as RectTransform;
                if (orig != null && rt != null && backBtn.transform.parent.GetComponent<HorizontalOrVerticalLayoutGroup>() == null)
                {
                    rt.anchoredPosition = orig.anchoredPosition + new Vector2(0f, -(orig.rect.height + 6f));
                }
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("upload button creation failed: " + e);
            }
        }

        private static Button FindButtonByMethod(Transform root, string methodName, RectTransform preferUnder)
        {
            Button fallback = null;
            foreach (var b in root.GetComponentsInChildren<Button>(true))
            {
                if (!PersistentMethods(b).Contains(methodName))
                {
                    continue;
                }
                if (preferUnder != null && b.transform.IsChildOf(preferUnder))
                {
                    return b;
                }
                fallback ??= b;
            }
            return fallback;
        }

        private static string PersistentMethods(Button b)
        {
            var sb = new StringBuilder();
            int n = b.onClick.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
            {
                sb.Append(b.onClick.GetPersistentMethodName(i)).Append(';');
            }
            return sb.ToString();
        }

        private static bool IsRowTemplate(Button b)
        {
            return _rowTemplate != null && (b.gameObject == _rowTemplate || b.transform.IsChildOf(_rowTemplate.transform));
        }

        private static void SetLabel(GameObject go, string text)
        {
            if (go == null)
            {
                return;
            }
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
            {
                t.text = text;
            }
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name) || root.name == name)
            {
                return root;
            }
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindChild(root.GetChild(i), name);
                if (r != null)
                {
                    return r;
                }
            }
            return null;
        }

        private static void Warn(string text)
        {
            UnifiedPopup.Push(new WarningPopup("Valheim Save Share", text, UnifiedPopup.Pop, localizeText: false));
        }
    }
}
