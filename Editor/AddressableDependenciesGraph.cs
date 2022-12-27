/***********************************************************************************************************
 * AddressablesDependenciesGraph.cs
 * Copyright (c) Yugo Fujioka - Unity Technologies Japan K.K.
 * 
 * Licensed under the Unity Companion License for Unity-dependent projects--see Unity Companion License.
 * https://unity.com/legal/licenses/unity-companion-license
 * Unless expressly provided otherwise, the Software under this license is made available strictly
 * on an "AS IS" BASIS WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.
 * Please review the license for details on these and other terms and conditions.
***********************************************************************************************************/

using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.UIElements;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace UTJ {
    /// <summary>
    /// Addressablesで自動解決した依存関係をノードグラフで確認する
    /// </summary>
    internal class AddressableDependenciesGraph : EditorWindow {

        [MenuItem("UTJ/ADDR Dependencies Graph")]
        public static void Open() {
            GetWindow<AddressableDependenciesGraph>().Show();
        }

        private GraphView graphView = null;
        private DependenciesRule bundleRule = new DependenciesRule();


        [System.Serializable]
        public class IgnorePrefix {
            public string text;
        }
        private List<IgnorePrefix> ignorePrefixList = new List<IgnorePrefix>();


        #region MAIN LAYOUT
        private void OnEnable() {
            this.titleContent = new GUIContent("ADDR Dependencies Graph");
            var settings = AddressableAssetSettingsDefaultObject.Settings;

            var mainBox = new Box();
            mainBox = new Box();
            mainBox.style.width = 300f;

            this.rootVisualElement.Add(mainBox);


            AddrUtility.CreateSpace(mainBox);

            // Select Group
            var groupList = settings.groups.FindAll(group => {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema != null && schema.IncludeInBuild)
                    return (schema.IncludeAddressInCatalog || schema.IncludeGUIDInCatalog || schema.IncludeLabelsInCatalog);
                return false;
            });
            var selectedGroupFiled = new PopupField<AddressableAssetGroup>("Selected Group", groupList, 0,
                value => value.name,
                value => value.name);
            selectedGroupFiled.name = "SelectedGroup";
            mainBox.Add(selectedGroupFiled);

            // Select Entry
            var entryList = new List<AddressableAssetEntry>() { null };
            entryList.AddRange(selectedGroupFiled.value.entries);
            var selectedEntryField = new PopupField<AddressableAssetEntry>("Selected Entry", entryList, 0,
                value => value == null ? "all" : value.address,
                value => value == null ? "all" : value.address);
            selectedEntryField.name = "SelectedEntry";
            mainBox.Add(selectedEntryField);

            // Options
            var sharedNodeToggle = AddrUtility.CreateToggle(mainBox,
                "Visible Shared Nodes",
                "自動生成されたSharedグループを表示します。依存関係が複雑な場合は時間がかかるので注意してください。",
                false);
            var shaderNodeToggle = AddrUtility.CreateToggle(mainBox,
                "Visible Shader Nodes",
                "自動生成されたShaderグループを表示します。Shader数が多いプロジェクトでは表示負荷が高いので注意してください",
                false);
            var depthInteger = AddrUtility.CreateInteger(mainBox,
                "Visible Depth",
                "依存関係の表示制限をかけます。Asset単位での依存関係を表示する際に便利です。0で無効となります。",
                0);
            this.CreateStringList(mainBox,
                "Ignore Keyword",
                "特定の文字列をグループ名に含む場合にノード表示を省略します。常駐グループを表示しない場合に設定してください。");

            // Groupが変更されたらEntryのリストを更新
            selectedGroupFiled.RegisterCallback<ChangeEvent<AddressableAssetGroup>>((ev) => {
                entryList.Clear();
                entryList.Add(null);
                foreach (var entry in ev.newValue.entries)
                    entryList.Add(entry);
                selectedEntryField.index = 0;
            });

            // Space
            AddrUtility.CreateSpace(mainBox);

            // Clear Analysis Button
            {
                AddrUtility.CreateHelpBox(mainBox, "Analyze結果をクリアします\nGroup設定の変更を反映する際に必要です");

                AddrUtility.CreateButton(mainBox, "Clear Addressables Analysis").clicked += () => {
                    this.bundleRule.Clear();
                    if (this.graphView != null) {
                        this.rootVisualElement.Remove(this.graphView);
                        this.graphView = null;
                    }
                };
            }

            // Space
            AddrUtility.CreateSpace(mainBox);

            // Bundle-Dependencies Button
            {
                AddrUtility.CreateHelpBox(mainBox, "AssetBundleの依存関係を表示します\n暗黙でロードされるbundleを確認できます");

                AddrUtility.CreateButton(mainBox, "View Bundle-Dependencies").clicked += () => {
                    this.rootVisualElement.Remove(mainBox);
                    if (this.graphView != null)
                        this.rootVisualElement.Remove(this.graphView);
                    this.bundleRule.Execute();

                    var entry = selectedEntryField.index > 0 ? selectedEntryField.value : null;
                    this.graphView = new BundlesGraph(BundlesGraph.TYPE.BUNDLE_DEPENDENCE,
                                                      selectedGroupFiled.value,
                                                      entry,
                                                      shaderNodeToggle.value,
                                                      sharedNodeToggle.value,
                                                      depthInteger.value,
                                                      this.ignorePrefixList,
                                                      this.bundleRule);
                    this.rootVisualElement.Add(this.graphView);
                    this.rootVisualElement.Add(mainBox);
                };
            }

            // Space
            AddrUtility.CreateSpace(mainBox);

            // Asset-Dependencies Button
            {
                AddrUtility.CreateHelpBox(mainBox, "AssetBundleに含まれるAssetの依存関係を表示します\n意図しない参照を見つけることができます");

                AddrUtility.CreateButton(mainBox, "View Asset-Dependencies").clicked += () => {
                    this.rootVisualElement.Remove(mainBox);
                    if (this.graphView != null)
                        this.rootVisualElement.Remove(this.graphView);
                    this.bundleRule.Execute();

                    var entry = selectedEntryField.index > 0 ? selectedEntryField.value : null;
                    this.graphView = new BundlesGraph(BundlesGraph.TYPE.ASSET_DEPENDENCE,
                                                      selectedGroupFiled.value,
                                                      entry,
                                                      shaderNodeToggle.value,
                                                      sharedNodeToggle.value,
                                                      depthInteger.value,
                                                      this.ignorePrefixList,
                                                      this.bundleRule);
                    this.rootVisualElement.Add(this.graphView);
                    this.rootVisualElement.Add(mainBox);
                };
            }
        }

        public void CreateStringList(VisualElement root, string title, string tooltip) {
            root.Add(new Label() { text = title, tooltip = tooltip, style = { paddingLeft = 4f } });

            var so = new SerializedObject(this);
            // NOTE:
            // GraphViewのNodeだとStyleを変更してもListViewを追加するとForcus範囲が縦いっぱいになる不具合がある
            // GraphViewやめてUIElements.Boxにする
            var listView = new ListView {
                bindingPath = "ignorePrefixList",
                reorderable = false,
                showAddRemoveFooter = true,
                showBorder = true,
                showFoldoutHeader = false,
                showBoundCollectionSize = false,
                makeItem = () => {
                    var root = new BindableElement();
                    var textField = new TextField {
                        bindingPath = "text",
                    };
                    root.Add(textField);
                    return root;
                }
            };
            listView.Bind(so);
            root.Add(listView);
        }
        #endregion


        #region BUNDLE RULE
        /// <summary>
        ///  Analyze
        /// </summary>
        public class DependenciesRule : BundleRuleBase {
            public AddressableAssetsBuildContext context { get; private set; }
            public ExtractDataTask extractData { get; private set; }
            public List<AssetBundleBuild> bunldeInfos { get; private set; }

            public delegate bool IsPathCallback(string path);
            public IsPathCallback IsPathValidForEntry { get; private set; }

            public DependenciesRule() {
                // Utilityの取得
                var aagAssembly = typeof(AddressableAssetGroup).Assembly;
                var aauType = aagAssembly.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetUtility");
                var validMethod = aauType.GetMethod("IsPathValidForEntry", BindingFlags.Static | BindingFlags.NonPublic, null, new System.Type[] { typeof(string) }, null);
                this.IsPathValidForEntry = System.Delegate.CreateDelegate(typeof(IsPathCallback), validMethod) as IsPathCallback;
            }
            public void Execute() {
                var settings = AddressableAssetSettingsDefaultObject.Settings;

                if (this.context == null) {
                    CalculateInputDefinitions(settings);
                    this.context = GetBuildContext(settings);

                    var exitCode = RefreshBuild(this.context);
                    if (exitCode < ReturnCode.Success) {
                        Debug.LogError($"Analyze build failed. {exitCode}");
                        return;
                    }

                    // Addressables 1.20以降はReflection不要
                    //this.extractData = this.ExtractData;
                    var extractDataField = this.GetType().GetField("m_ExtractData", BindingFlags.Instance | BindingFlags.NonPublic);
                    this.extractData = (ExtractDataTask)extractDataField.GetValue(this);

                    var bundleInfoField = this.GetType().GetField("m_AllBundleInputDefs", BindingFlags.Instance | BindingFlags.NonPublic);
                    this.bunldeInfos = (List<AssetBundleBuild>)bundleInfoField.GetValue(this);
                }
            }

            public void Clear() {
                this.context = null;
                this.extractData = null;
                this.bunldeInfos = null;

                // Analyze共通処理
                ClearAnalysis();
                if (!BuildUtility.CheckModifiedScenesAndAskToSave()) {
                    Debug.LogError("Cannot run Analyze with unsaved scenes");
                    return;
                }
            }
        }
        #endregion


        #region NODE
        /// <summary>
        /// ノード拡張
        /// </summary>
        public class BundleNode : Node {
            public string bundleName = string.Empty;
            public Dictionary<string, Port> input = new Dictionary<string, Port>();     // InputContainerに登録されているPort
            public Dictionary<string, Port> output = new Dictionary<string, Port>();    // OutputContainerに登録されているPort
            public Dictionary<string, GUID> assetGuid = new Dictionary<string, GUID>(); // Portに登録されているAssetのGUID（※Asset依存関係で使うのでキャッシュする）
            public Dictionary<Port, HashSet<Port>> connectTo = new Dictionary<Port, HashSet<Port>>();       // 接続されたPort
            public Dictionary<Port, List<FlowingEdge>> edgeTo = new Dictionary<Port, List<FlowingEdge>>();  // 接続されたEdge
            public Dictionary<Port, List<FlowingEdge>> edgeFrom = new Dictionary<Port, List<FlowingEdge>>();// 接続されたEdge

            public BundleNode() {
                this.capabilities &= ~Capabilities.Deletable; // 削除を禁止
            }

            public override void OnSelected() {
                base.OnSelected();

                foreach (var pair in edgeTo) {
                    foreach (var edge in pair.Value)
                        edge.activeFlow = true;
                }
                foreach (var pair in edgeFrom) {
                    foreach (var edge in pair.Value)
                        edge.selected = true; // 色をOutputとは変える
                }
            }
            public override void OnUnselected() {
                base.OnUnselected();

                foreach (var pair in edgeTo) {
                    foreach (var edge in pair.Value)
                        edge.activeFlow = false;
                }
                foreach (var pair in edgeFrom) {
                    foreach (var edge in pair.Value)
                        edge.selected = false;
                }
            }
        }
        #endregion


        #region GraphView
        /// <summary>
        /// Graph表示
        /// </summary>
        public class BundlesGraph : GraphView {

            public enum TYPE {
                BUNDLE_DEPENDENCE, // bundle間の依存関係
                ASSET_DEPENDENCE,  // bundle内のAsset間の依存関係
            }

            private const string UNITY_BUILTIN_SHADERS = "unitybuiltinshaders";
            private GUID UNITY_BUILTIN_SHADERS_GUID = new GUID("0000000000000000e000000000000000"); // SBPのCommonSettings.csから

            private const float NODE_OFFSET_H = 140f;
            private const float NODE_OFFSET_V = 20f;

            //private BundleNode shaderNode, builtinNode;
            private List<FlowingEdge> allEdges = new List<FlowingEdge>();
            private Dictionary<string, BundleNode> bundleNodes = new Dictionary<string, BundleNode>();
            private List<string> explicitNodes = new List<string>();

            /// <summary>
            /// Bundleの全依存関係
            /// </summary>
            public BundlesGraph(TYPE type, AddressableAssetGroup group, AddressableAssetEntry entry, bool enabledShaderNode, bool enabledSharedNode, int visibleDepth, List<IgnorePrefix> ignoreList, DependenciesRule rule) {
                switch (type) {
                    case TYPE.BUNDLE_DEPENDENCE:
                        this.ViewBundles(rule, group, entry, enabledSharedNode, ignoreList);
                        break;
                    case TYPE.ASSET_DEPENDENCE:
                        this.ViewEntries(rule, group, entry, enabledSharedNode, ignoreList);
                        break;
                }
                // NOTE: レイアウトが一旦完了しないとノードサイズがとれないのでViewの描画前コールバックに仕込む
                var position = new Vector2(0f, 50f);
                this.RegisterCallback<GeometryChangedEvent>((ev) => {
                    var position = new Vector2(400f, 50f);
                    var parentStack = new HashSet<string>(); // 親ノード
                    var placedNames = new HashSet<string>(); // 整列済みノード
                    foreach (var bundleName in this.explicitNodes)
                        position = this.AlignNode(rule.context, bundleName, parentStack, placedNames, enabledShaderNode, visibleDepth, position);
                });

                this.StretchToParentSize(); // 親のサイズに合わせてGraphViewのサイズを設定
                this.SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale); // スクロールズーム
                this.AddManipulator(new ContentDragger());      // 描画範囲ドラッグ
                this.AddManipulator(new SelectionDragger());    // 選択ノードドラッグ
                this.AddManipulator(new RectangleSelector());   // 範囲ノード選択
            }

            bool IsShaderGroupNode(BundleNode node) {
                return node.title.Contains(AddressablesAutoGrouping.SHADER_GROUP_NAME);
            }
            bool IsBuiltInShaderNode(BundleNode node) {
                return node.bundleName.Contains(UNITY_BUILTIN_SHADERS);
            }


            #region PRIVATE FUNCTION
            /// <summary>
            /// ノード整列
            /// </summary>
            private Vector2 AlignNode(AddressableAssetsBuildContext context, string bundleName, HashSet<string> parentStack, HashSet<string> placedNodes, bool enabledShaderNode, int visibleDepth, Vector2 position) {
                if (this.bundleNodes.TryGetValue(bundleName, out var node)) {
                    if (!enabledShaderNode) {
                        //if (node.title == this.shaderNode || node == this.builtinNode) {
                        if (this.IsShaderGroupNode(node) || this.IsBuiltInShaderNode(node)) {
                            node.visible = false;
                            foreach (var edgeList in node.edgeFrom.Values) {
                                foreach (var edge in edgeList)
                                    edge.visible = false;
                            }
                            return position;
                        }
                    }

                    var rect = node.GetPosition();

                    if (!placedNodes.Contains(bundleName)) {
                        rect.x = position.x;
                        rect.y = position.y;
                        node.SetPosition(rect);
                        placedNodes.Add(bundleName);
                    }

                    var addChild = false;
                    // 表示深度制限
                    if (visibleDepth > 0 && parentStack.Count > visibleDepth) {
                        node.visible = false;
                        foreach (var edgeList in node.edgeFrom.Values) {
                            foreach (var edge in edgeList)
                                edge.visible = false;
                        }
                        foreach (var edgeList in node.edgeTo.Values) {
                            foreach (var edge in edgeList)
                                edge.visible = false;
                        }
                    } else {
                        parentStack.Add(bundleName);

                        if (context.bundleToImmediateBundleDependencies.TryGetValue(bundleName, out var depBundleNames)) {
                            // アルファベット順にソート
                            depBundleNames.Sort(CompareGroup);

                            if (depBundleNames.Count > 1) {
                                var pos = position;
                                pos.x += rect.width + NODE_OFFSET_H;
                                foreach (var depBundleName in depBundleNames) {
                                    // 自身は無視
                                    if (depBundleName == bundleName)
                                        continue;
                                    // 循環参照
                                    if (parentStack.Contains(depBundleName)) {
                                        if (this.bundleNodes.TryGetValue(depBundleName, out var depNode))
                                            Debug.LogWarning($"循環参照 : {node.title} <-> {depNode.title}");
                                        continue;
                                    }
                                    if (placedNodes.Contains(depBundleName))
                                        continue;

                                    pos = this.AlignNode(context, depBundleName, parentStack, placedNodes, enabledShaderNode, visibleDepth, pos);
                                    position.y = pos.y;
                                    addChild = true;
                                }
                            }
                        }

                        parentStack.Remove(bundleName);

                        if (!addChild)
                            position.y += rect.height + NODE_OFFSET_V;
                        position.y = Mathf.Max(position.y, rect.y + rect.height + NODE_OFFSET_V);
                    }
                }

                return position;
            }

            /// <summary>
            /// 新規ノード
            /// </summary>
            /// <param name="bundleName">AssetBundle名</param>
            /// <param name="isExplicit">明示的に呼ばれる（カタログに追加されている）グループか</param>
            /// <param name="createSharedNode">Sharedノード有効か</param>
            private BundleNode CreateBundleNode(AddressableAssetsBuildContext context, string bundleName, bool isExplicit, bool createSharedNode, List<IgnorePrefix> ignoreList) {
                if (this.bundleNodes.TryGetValue(bundleName, out var node)) {
                    Debug.LogWarning($"Exist the same bundleName for Nodes : {bundleName}");
                    return node;
                }

                var title = string.Empty;
                //var isBuiltIn = false;
                //var isShaderNode = false; // Shader
                // ノード名
                if (bundleName.Contains(UNITY_BUILTIN_SHADERS)) {
                    title = "Unity Built-in Shaders";
                    //isBuiltIn = true;
                } else {
                    // Hashを取り除いてグループ名と結合
                    var temp = System.IO.Path.GetFileName(bundleName).Split(new string[] { "_assets_", "_scenes_" }, System.StringSplitOptions.None);
                    title = temp[temp.Length - 1];
                    if (context.bundleToAssetGroup.TryGetValue(bundleName, out var groupGUID)) {
                        var groupName = context.Settings.FindGroup(findGroup => findGroup != null && findGroup.Guid == groupGUID).name;
                        title = $"{groupName}/{title}";

                        //isShaderNode = (this.shaderNode == null && groupName == AddressablesAutoGrouping.SHADER_GROUP_NAME);
                    }
                }

                // プロジェクト規模が大きくなると表示しきれないので表示量を減らす為の措置
                if (!isExplicit) {
                    // 任意名無視
                    foreach (var ignore in ignoreList) {
                        if (title.Contains(ignore.text))
                            return null;
                    }
                    // 依存グループ無視
                    if (!createSharedNode && title.Contains("Shared-"))
                        return null;
                }

                node = new BundleNode();
                node.title = title;
                node.name = node.bundleName = bundleName;
                //if (isBuiltIn)
                //    this.builtinNode = node;
                //if (isShaderNode)
                //    this.shaderNode = node;

                // 明示的なノードは黄色
                if (isExplicit)
                    node.titleContainer.style.backgroundColor = new Color(0.3f, 0.3f, 0f, 1f);

                this.AddElement(node);
                this.bundleNodes.Add(bundleName, node);

                // タイトル横のボタン削除
                node.titleButtonContainer.Clear();
                node.titleButtonContainer.Add(new Label(string.Empty)); // NOTE: 空白入れないと詰められて見た目が悪い

                return node;
            }

            /// <summary>
            /// Inputポートの作成
            /// </summary>
            private Port CreateInputPort(BundleNode node, string portName, string keyName) {
                var input = Port.Create<FlowingEdge>(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(float));
                input.portName = portName;
                input.capabilities = 0;
                node.inputContainer.Add(input);
                node.input.Add(keyName, input);
                return input;
            }

            /// <summary>
            /// Edge接続
            /// </summary>
            private void AddEdge(BundleNode fromNode, Port from, BundleNode toNode, Port to) {
                var addEdge = false;
                if (fromNode.connectTo.TryGetValue(from, out var ports)) {
                    // 2回目以降の接続
                    // NOTE: 複数のSubAssetが同一のAssetに依存関係を持つ場合に発生するので弾く、主にSceneで発生
                    if (!ports.Contains(to))
                        addEdge = true;
                } else {
                    // 初回接続
                    addEdge = true;
                    ports = new HashSet<Port>();
                    fromNode.connectTo.Add(from, ports);
                }
                if (addEdge) {
                    // 新規Edge作成
                    var edge = from.ConnectTo<FlowingEdge>(to);
                    this.AddElement(edge);
                    if (!fromNode.edgeTo.TryGetValue(from, out var toEdges)) {
                        toEdges = new List<FlowingEdge>();
                        fromNode.edgeTo.Add(from, toEdges);
                    }
                    if (!toNode.edgeFrom.TryGetValue(to, out var fromEdges)) {
                        fromEdges = new List<FlowingEdge>();
                        toNode.edgeFrom.Add(to, fromEdges);
                    }
                    toEdges.Add(edge);
                    fromEdges.Add(edge);
                    ports.Add(to);
                    this.allEdges.Add(edge);
                }
            }

            static System.Text.RegularExpressions.Regex NUM_REGEX = new System.Text.RegularExpressions.Regex(@"[^0-9]");
            private static int CompareGroup(string a, string b) {
                if (a.Contains(UNITY_BUILTIN_SHADERS))
                    return -1;
                if (b.Contains(UNITY_BUILTIN_SHADERS))
                    return 1;

                var ret = string.CompareOrdinal(a, b);
                // 桁数の違う数字を揃える
                var regA = NUM_REGEX.Replace(a, string.Empty);
                var regB = NUM_REGEX.Replace(b, string.Empty);
                if ((regA.Length > 0 && regB.Length > 0) && regA.Length != regB.Length) {
                    if (ret > 0 && regA.Length < regB.Length)
                        return -1;
                    else if (ret < 0 && regA.Length > regB.Length)
                        return 1;
                }

                return ret;
            }
            private static int CompareGroup(VisualElement a, VisualElement b) {
                return CompareGroup(a.name, b.name);
            }
            #endregion


            #region BUNDLES
            /// <summary>
            /// 内容物をOutputポートに登録
            /// </summary>
            private void CreateOutputPort(BundleNode node) {
                if (node.bundleName.Contains(UNITY_BUILTIN_SHADERS))
                    return;

                var output = Port.Create<FlowingEdge>(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(float));
                output.portName = string.Empty;
                output.capabilities = 0;
                node.outputContainer.Add(output);
                node.output.Add(node.bundleName, output);
            }
            
            void AddBundleNode(DependenciesRule rule, BundleNode parentNode, string bundleName, bool enabledSharedNode, List<IgnorePrefix> ignoreList) {
                var onlyConnect = false;
                if (this.bundleNodes.TryGetValue(bundleName, out var node)) {
                    onlyConnect = true;
                } else {
                    // 新規登録
                    node = this.CreateBundleNode(rule.context, bundleName, false, enabledSharedNode, ignoreList);
                    // 無視されるノードの場合はnull
                    if (node == null)
                        return;
                }

                // 接続ポート
                Port input = null;
                if (node.inputContainer.childCount == 0)
                    input = this.CreateInputPort(node, string.Empty, parentNode.bundleName); // 新規Port作成
                else
                    input = node.inputContainer.ElementAt(0) as Port;

                // 接続
                var parentPort = parentNode.outputContainer.ElementAt(0) as Port;
                if (rule.context.bundleToImmediateBundleDependencies.TryGetValue(parentNode.bundleName, out var depNames)) {
                    if (depNames.Contains(bundleName))
                        this.AddEdge(parentNode, parentPort, node, input);
                }

                if (onlyConnect)
                    return;

                // 依存先がある場合は作成
                if (rule.context.bundleToImmediateBundleDependencies.TryGetValue(bundleName, out var depBundleNames)) {
                    // アルファベット順にソート
                    depBundleNames.Sort(CompareGroup);

                    if (depBundleNames.Count > 1) {
                        var output = Port.Create<FlowingEdge>(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(float));
                        output.portName = name;
                        output.capabilities = 0;
                        node.outputContainer.Add(output);

                        // 再帰的に辿る
                        foreach (var depBundleName in depBundleNames) {
                            // 自分も含まれるのでスキップ
                            if (bundleName == depBundleName)
                                continue;

                            this.AddBundleNode(rule, node, depBundleName, enabledSharedNode, ignoreList);
                        }
                    }
                }

                node.RefreshPorts();
                node.RefreshExpandedState();
            }

            void ViewBundles(DependenciesRule rule, AddressableAssetGroup group, AddressableAssetEntry entry, bool enabledSharedNode, List<IgnorePrefix> ignoreList) {
                this.bundleNodes.Clear();
                var context = rule.context;

                if (context.assetGroupToBundles.TryGetValue(group, out var bundleNames)) {
                    foreach (var bundleName in bundleNames) {
                        if (bundleName.Contains(UNITY_BUILTIN_SHADERS))
                            continue;

                        // 指定エントリ名でフィルタリング
                        if (entry != null) {
                            var hit = false;
                            var bundleInfo = rule.bunldeInfos.Find(val => val.assetBundleName == bundleName);
                            foreach (var assetName in bundleInfo.assetNames) {
                                if (assetName == entry.AssetPath) {
                                    hit = true;
                                    break;
                                }
                            }
                            if (!hit)
                                continue;
                        }

                        var node = this.CreateBundleNode(context, bundleName, true, true, ignoreList);
                        this.explicitNodes.Add(bundleName);

                        // implicitノード作成
                        if (context.bundleToImmediateBundleDependencies.TryGetValue(bundleName, out var depBundleNames)) {
                            if (depBundleNames.Count > 1) {
                                this.CreateOutputPort(node);

                                // 再帰的に辿る
                                foreach (var depBundleName in depBundleNames) {
                                    // 自分も含まれるのでスキップ
                                    if (bundleName == depBundleName)
                                        continue;

                                    this.AddBundleNode(rule, node, depBundleName, enabledSharedNode, ignoreList);
                                }
                            }
                        }
                    }
                }

                // 不要なPortを取り除く
                foreach (var node in this.bundleNodes.Values) {
                    node.RefreshPorts();
                    node.RefreshExpandedState();
                }
            }
            #endregion


            #region ENTRIES
            /// <summary>
            /// 内容物をOutputポートに登録
            /// </summary>
            private void CreateOutputPortsWithAssets(DependenciesRule rule, BundleNode node) {
                if (node.bundleName.Contains(UNITY_BUILTIN_SHADERS)) {
                    node.assetGuid.Add(UNITY_BUILTIN_SHADERS, UNITY_BUILTIN_SHADERS_GUID);
                    return;
                }

                // 内容物登録
                var info = rule.bunldeInfos.Find((info) => {
                    return info.assetBundleName == node.bundleName;
                });

                foreach (var assetName in info.assetNames) {
                    var output = Port.Create<FlowingEdge>(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(float));
                    output.portName = assetName;
                    output.capabilities = 0;
                    node.outputContainer.Add(output);
                    node.output.Add(assetName, output);
                    node.assetGuid.Add(assetName, new GUID(AssetDatabase.AssetPathToGUID(assetName)));
                }

                // アルファベット順
                node.outputContainer.Sort(CompareGroup);
            }

            int AddEntriesNode(DependenciesRule rule, BundleNode parentNode, string bundleName, bool enabledSharedNode, List<IgnorePrefix> ignoreList) {
                var onlyConnect = false;

                if (this.bundleNodes.TryGetValue(bundleName, out var node)) {
                    onlyConnect = true;
                } else {
                    // 新規登録
                    node = this.CreateBundleNode(rule.context, bundleName, false, enabledSharedNode, ignoreList);
                    // 無視されるノードの場合はnull
                    if (node == null)
                        return 0;

                    // 内容物表示
                    this.CreateOutputPortsWithAssets(rule, node);
                }

                // 親のエントリアセットを走査
                foreach (var pair in parentNode.output) {
                    // ExtractData.DependencyDataからAssetの依存関係を取得
                    // NOTE: AssetDatabase.GetDependenciesでも取れるがすこぶる遅いので却下
                    var parentAsset = pair.Key;
                    var parentPort = pair.Value;
                    var parentGuid = new GUID(AssetDatabase.AssetPathToGUID(parentAsset));
                    ICollection<ObjectIdentifier> parentObjects = null;

                    // Sceneは特殊なので分ける
                    if (parentAsset.Contains(".unity")) {
                        if (rule.extractData.DependencyData.SceneInfo.TryGetValue(parentGuid, out var parentDependencies))
                            parentObjects = parentDependencies.referencedObjects;
                    } else {
                        if (rule.extractData.DependencyData.AssetInfo.TryGetValue(parentGuid, out var parentDependencies))
                            parentObjects = parentDependencies.referencedObjects;
                    }
                    if (parentObjects == null)
                        continue;

                    foreach (var parentObj in parentObjects) {
                        // Built-in Shader対応
                        if (parentObj.guid == UNITY_BUILTIN_SHADERS_GUID) {
                            if (node.assetGuid.ContainsKey(UNITY_BUILTIN_SHADERS)) {
                                Port input = null;
                                if (node.inputContainer.childCount == 0)
                                    input = this.CreateInputPort(node, string.Empty, UNITY_BUILTIN_SHADERS); // 新規Port作成
                                else
                                    input = node.inputContainer.ElementAt(0) as Port;

                                // Port間接続
                                this.AddEdge(parentNode, parentPort, node, input);
                            }
                            continue;
                        }

                        // スクリプトだとかを除外
                        var parentPath = AssetDatabase.GUIDToAssetPath(parentObj.guid);
                        if (!rule.IsPathValidForEntry(parentPath))
                            continue;
                        // Prefabは依存関係にならないので除外
                        if (parentPath.Contains(".prefab"))
                            continue;

                        // ノード接続
                        foreach (var myAssetName in node.output.Keys) {
                            if (node.assetGuid.TryGetValue(myAssetName, out var myGuid)) {
                                // MainAsset判定
                                var hit = (parentObj.guid == myGuid);
                                // SubAsset検索
                                if (!hit) {
                                    if (rule.extractData.DependencyData.AssetInfo.TryGetValue(myGuid, out var myAllAssets)) {
                                        foreach (var refAsset in myAllAssets.referencedObjects) {
                                            // NOTE: インスタンス作成するのでメモリオーバーヘッドがあるが素直にSubAsset判定した方がセーフティ
                                            var instance = ObjectIdentifier.ToObject(refAsset);
                                            if (instance == null || !AssetDatabase.IsSubAsset(instance))
                                                continue;
                                            if (parentObj == refAsset) {
                                                hit = true;
                                                break;
                                            }
                                            //if (parentObj == refAsset) {
                                            //    // 参照されているがGUIDが独立していればSubAssetとみなさない
                                            //    // e.g. fbxに含まれるMaterial/Texture/ShaderはGUIDが振られるのでSubAssetではない
                                            //    if (parentObj.guid != refAsset.guid)
                                            //        hit = true;
                                            //    break;
                                            //}
                                        }
                                    }
                                }

                                // 依存関係を発見
                                if (hit) {
                                    if (!node.input.TryGetValue(myAssetName, out var input))
                                        input = this.CreateInputPort(node, myAssetName, myAssetName); // 新規Port作成

                                    // Port間接続
                                    this.AddEdge(parentNode, parentPort, node, input);
                                }
                            }
                        }
                    }
                }

                if (onlyConnect)
                    return 0;

                var totalDepCount = 0;
                // 依存先がある場合は作成
                if (rule.context.bundleToImmediateBundleDependencies.TryGetValue(bundleName, out var depBundleNames)) {
                    if (depBundleNames.Count > 1) {
                        // 再帰的に辿る
                        foreach (var depBundleName in depBundleNames) {
                            // 自分も含まれるのでスキップ
                            if (bundleName == depBundleName)
                                continue;

                            totalDepCount += this.AddEntriesNode(rule, node, depBundleName, enabledSharedNode, ignoreList);
                        }
                    }
                }
                totalDepCount = Mathf.Max(totalDepCount, 1);

                return totalDepCount;
            }

            void ViewEntries(DependenciesRule rule, AddressableAssetGroup group, AddressableAssetEntry entry, bool enabledSharedNode, List<IgnorePrefix> ignoreList) {
                this.bundleNodes.Clear();
                var context = rule.context;

                if (context.assetGroupToBundles.TryGetValue(group, out var bundleNames)) {
                    foreach (var bundleName in bundleNames) {
                        if (bundleName.Contains(UNITY_BUILTIN_SHADERS))
                            continue;

                        // 指定エントリ名でフィルタリング
                        if (entry != null) {
                            var hit = false;
                            var bundleInfo = rule.bunldeInfos.Find(val => val.assetBundleName == bundleName);
                            foreach (var assetName in bundleInfo.assetNames) {
                                if (assetName == entry.AssetPath) {
                                    hit = true;
                                    break;
                                }
                            }
                            if (!hit)
                                continue;
                        }

                        // explicitノード作成
                        var node = this.CreateBundleNode(rule.context, bundleName, true, enabledSharedNode, ignoreList);
                        this.explicitNodes.Add(bundleName);

                        // 内容物表示
                        this.CreateOutputPortsWithAssets(rule, node);

                        // implicitノード作成
                        var totalDepth = 0;
                        if (context.bundleToImmediateBundleDependencies.TryGetValue(bundleName, out var depBundleNames)) {
                            if (depBundleNames.Count > 1) {
                                // 再帰的に辿る
                                foreach (var depBundleName in depBundleNames) {
                                    // 自分も含まれるのでスキップ
                                    if (bundleName == depBundleName)
                                        continue;

                                    var depCount = this.AddEntriesNode(rule, node, depBundleName, enabledSharedNode, ignoreList);
                                    totalDepth += depCount;
                                }
                            }
                        }
                    }

                    // 不要なPortを取り除く
                    foreach (var node in this.bundleNodes.Values) {
                        node.RefreshPorts();
                        node.RefreshExpandedState();
                    }
                }
            }
            #endregion

            // MEMO: 右クリックのコンテキストメニュー
            public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) {
                // 今回は確認専用ビューなので空にする（何も出さない）
            }
        }
        #endregion
    }
}