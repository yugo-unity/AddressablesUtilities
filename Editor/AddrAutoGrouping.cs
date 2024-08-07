/***********************************************************************************************************
 * AddrAutoGrouping.cs
 * Copyright (c) Yugo Fujioka - Unity Technologies Japan K.K.
 * 
 * Licensed under the Unity Companion License for Unity-dependent projects--see Unity Companion License.
 * https://unity.com/legal/licenses/unity-companion-license
 * Unless expressly provided otherwise, the Software under this license is made available strictly
 * on an "AS IS" BASIS WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.
 * Please review the license for details on these and other terms and conditions.
***********************************************************************************************************/

using System.Reflection;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using UnityEngine.U2D;

namespace UTJ
{
    /// <summary>
    /// Addressablesの最適な重複解決の自動グルーピング
    /// 暗黙の依存Asset（ImplicitAssets）を検出して同じ依存関係を持つAssetとグループ（＝Assetbundle）をまとめる
    /// </summary>
    public static class AddrAutoGrouping
    {
        public const string SHARED_GROUP_NAME = "+Shared_";
        public const string SHADER_GROUP_NAME = "+Shared_Shader";
        public const string SINGLE_GROUP_NAME = "+Shared_Single";
        public const string RESIDENT_GROUP_NAME = "+Residents";

        // delegate long GetMemorySizeLongCallback(Texture tex);
        // GetMemorySizeLongCallback GetStorageMemorySizeLong = null;

        /// <summary>
        /// SharedAssetグループの情報
        /// </summary>
        class SharedGroupParam
        {
            public SharedGroupParam(string name, List<string> bundles)
            {
                this.name = name;
                this.bundles = bundles;
            }

            public readonly string name;
            public readonly List<string> bundles; // 依存先のbundle
            public readonly List<ImplicitParam> implicitParams = new(); // 含まれる暗黙のAsset
        }

        /// <summary>
        /// 暗黙の依存Assetの収集情報
        /// </summary>
        class ImplicitParam
        {
            public string guid;
            public string path;
            public bool isSubAsset; // SubAssetかどうか
            public bool isResident; // 常駐アセットか
            public List<System.Type> usedType; // 使用されているSubAssetの型（fbxと用）

            public List<string> bundles; // 参照されているBundle
            //public long fileSize; // Assetのファイルサイズ
        }

        /// <summary>
        /// SpriteAtlasの収集情報
        /// </summary>
        class SpriteAtlasParam
        {
            public bool isResident;
            public SpriteAtlas instance;
        }

        /// <summary>
        /// 自動生成されたGroupかどうか
        /// </summary>
        /// <param name="group">Addressables Group</param>
        public static bool IsAutoGroup(AddressableAssetGroup group)
        {
            return group.Name.Contains(SHARED_GROUP_NAME) ||
                   group.Name.Contains(SHADER_GROUP_NAME) ||
                   group.Name.Contains(SINGLE_GROUP_NAME) ||
                   group.Name.Contains(RESIDENT_GROUP_NAME);
        }

        /// <summary>
        /// 重複アセット解決の実行
        /// </summary>
        /// <param name="groupingSettings">自動グルーピング設定</param>
        /// <returns>再帰処理するか</returns>
        public static bool Execute(AddrAutoGroupingSettings groupingSettings)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;

            // // 単品のbundleにするAssetのファイルサイズの閾値
            // var SEPARATE_ASSET_SIZE = (long)groupingSettings.singleThreshold * 1024L;

            // Analyze共通処理
            if (!BuildUtility.CheckModifiedScenesAndAskToSave())
            {
                Debug.LogError("Cannot run Analyze with unsaved scenes");
                return false;
            }

            //var exitCode = AddrUtility.CalculateBundleWriteData(out var aaContext, out var extractData);
            var allBundleInputDefs = new List<AssetBundleBuild>();
            var bundleToAssetGroup = new Dictionary<string, string>();
            AddrUtility.CalculateInputDefinitions(settings, allBundleInputDefs, bundleToAssetGroup);
            var aaContext = AddrUtility.GetBuildContext(settings, bundleToAssetGroup);
            var extractData = new ExtractDataTask();
            var exitCode = AddrUtility.RefleshBuild(settings, allBundleInputDefs, extractData, aaContext);
            if (exitCode < ReturnCode.Success)
            {
                Debug.LogError($"Analyze build failed. {exitCode}");
                return false;
            }

            // 暗黙の依存Asset情報を抽出
            var (implicitParams, atlases) =
                CollectImplicitParams(aaContext.bundleToAssetGroup, extractData.WriteData, groupingSettings);

            // 既に配置されてるSharedAssetグループ数
            var sharedGroupCount = settings.groups.FindAll(group => group.name.Contains(SHARED_GROUP_NAME)).Count;

            var sharedGroupParams = new List<SharedGroupParam>();
            var shaderGroupParam = new SharedGroupParam(SHADER_GROUP_NAME, null);
            var singleGroupParam = new SharedGroupParam(SINGLE_GROUP_NAME, null);
            var residentGroupParam = new SharedGroupParam(RESIDENT_GROUP_NAME, null);

            foreach (var implicitParam in implicitParams)
            {
                // 重複している常駐アセットは一つのGroupにまとめる
                var residentAsset = implicitParam.isResident && implicitParam.bundles.Count > 1;

                // Spriteはかなり例外処理なのでSpriteAtlas確認が必要
                if (implicitParam.usedType.Contains(typeof(Sprite)))
                {
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(implicitParam.path);
                    var packed = false;
                    foreach (var atlas in atlases)
                    {
                        // NOTE: SpriteAtlasに含まれているどうかがインスタンスでないとわからない...？
                        if (atlas.instance.CanBindTo(sprite))
                        {
                            // NOTE: SpriteAtlasに含まれているSpriteは無視
                            packed = implicitParam.usedType.Count == 1;
                            // NOTE: SpriteAtlasに含まれているSpriteの元テクスチャが参照されている、かつ
                            //       元テクスチャは常駐アセットと依存関係を持たない場合、
                            //       SpriteAtlasが常駐なら元Textureも常駐扱いとする
                            //       結局常駐グループに依存関係を持つため循環参照により不要なbundleが生成されてしまうのを避ける
                            residentAsset |= atlas.isResident;

                            break;
                        }
                    }

                    if (packed)
                        continue;
                }

                if (residentAsset)
                {
                    residentGroupParam.implicitParams.Add(implicitParam);
                    continue;
                }

                // Shader検出
                // NOTE: 常駐のShaderは常駐グループにまとめられる
                if (groupingSettings.shaderGroup)
                {
                    // Shaderグループにまとめる
                    var assetType = implicitParam.usedType[0];
                    if (assetType == typeof(Shader))
                    {
                        shaderGroupParam.implicitParams.Add(implicitParam);
                        continue;
                    }
                }

                // // 指定サイズより大きい場合は単品のbundleにする
                // if (SEPARATE_ASSET_SIZE > 0 && implicitParam.fileSize > SEPARATE_ASSET_SIZE)
                // {
                //     singleGroupParam.implicitParams.Add(implicitParam);
                //     continue;
                // }

                // 非重複Assetは何もしない
                if (implicitParam.bundles.Count == 1)
                    continue;

                // 既存検索
                var hit = sharedGroupParams.Count > 0; // 初回対応
                foreach (var groupParam in sharedGroupParams)
                {
                    // まず依存数（重複数）が違う
                    if (groupParam.bundles.Count != implicitParam.bundles.Count)
                    {
                        hit = false;
                        continue;
                    }

                    // 依存先（重複元）が同一のbundleか
                    hit = true;
                    foreach (var bundle in implicitParam.bundles)
                    {
                        if (!groupParam.bundles.Contains(bundle))
                        {
                            hit = false;
                            break;
                        }
                    }

                    if (hit)
                    {
                        groupParam.implicitParams.Add(implicitParam);
                        break;
                    }
                }

                // 新規Group
                if (!hit)
                {
                    var param = new SharedGroupParam(SHARED_GROUP_NAME + "{0}", implicitParam.bundles);
                    param.implicitParams.Add(implicitParam);
                    sharedGroupParams.Add(param);
                }
            }

            var continued = sharedGroupParams.Count > 0;

            // 常駐グループ
            if (residentGroupParam.implicitParams.Count > 0)
                sharedGroupParams.Add(residentGroupParam);

            // Shaderグループ
            if (groupingSettings.shaderGroup)
                sharedGroupParams.Add(shaderGroupParam);

            // 単一Group振り分け
            var singleGroup = CreateSharedGroup(settings, SINGLE_GROUP_NAME, groupingSettings.hashName);
            var schema = singleGroup.GetSchema<BundledAssetGroupSchema>();
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            CreateOrMoveEntry(settings, singleGroup, singleGroupParam);

            // Group振り分け
            foreach (var groupParam in sharedGroupParams)
            {
                // 1個しかAssetがないGroupは単一グループにまとめる
                var group = singleGroup;

                if (groupParam.implicitParams.Count > 1)
                {
                    var name = string.Format(groupParam.name, sharedGroupCount);
                    group = CreateSharedGroup(settings, name, groupingSettings.hashName);
                    sharedGroupCount++;
                }

                CreateOrMoveEntry(settings, group, groupParam);
            }

            // 空だったら不要
            if (singleGroup.entries.Count == 0)
                settings.RemoveGroup(singleGroup);

            // alphanumericソート
            settings.groups.Sort(AddrUtility.CompareGroup);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, eventData: null,
                postEvent: true, settingsModified: true);

            return continued;
        }

        /// <summary>
        /// SharedAsset用のGroupの作成
        /// </summary>
        static AddressableAssetGroup CreateSharedGroup(AddressableAssetSettings settings, string groupName,
            bool useHashName)
        {
            // Shared-SingleとShared-Shaderは単一
            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                var groupTemplate = settings.GetGroupTemplateObject(0) as AddressableAssetGroupTemplate;
                if (groupTemplate == null)
                {
                    Debug.LogError("Not found AddressableAssetGroupTemplate");
                    return null;
                }

                group = settings.CreateGroup(groupName, false, true, false,
                    groupTemplate.SchemaObjects);
            }

            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
                schema = group.AddSchema<BundledAssetGroupSchema>();

            // NOTE: 必ず無効にしないと反映されない
            schema.UseDefaultSchemaSettings = false;

            // NOTE: 依存Assetなのでcatalogに登録は省略（catalog.jsonの削減）
            schema.IncludeAddressInCatalog = false;
            schema.IncludeGUIDInCatalog = false;
            schema.IncludeLabelsInCatalog = false;

            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            schema.AssetLoadMode = UnityEngine.ResourceManagement.ResourceProviders.AssetLoadMode
                .AllPackedAssetsAndDependencies;
            schema.InternalBundleIdMode = BundledAssetGroupSchema.BundleInternalIdMode.GroupGuid;
            schema.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.Dynamic;
            schema.UseAssetBundleCrc = schema.UseAssetBundleCache = false;
            if (useHashName)
                schema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.FileNameHash;
            else
                schema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.NoHash;

            return group;
        }

        /// <summary>
        /// 指定Groupへエントリ
        /// </summary>
        static void CreateOrMoveEntry(AddressableAssetSettings settings, AddressableAssetGroup group,
            SharedGroupParam groupParam)
        {
            foreach (var implicitParam in groupParam.implicitParams)
            {
                var entry = settings.CreateOrMoveEntry(implicitParam.guid, group, false, false);
                var addr = System.IO.Path.GetFileNameWithoutExtension(implicitParam.path);
                entry.SetAddress(addr, false);
            }
        }

        /// <summary>
        /// 暗黙の依存Assetを抽出して情報をまとめる
        /// </summary>
        static (List<ImplicitParam>, List<SpriteAtlasParam>) CollectImplicitParams(
            Dictionary<string, string> bundleToAssetGroup,
            IBundleWriteData writeData, AddrAutoGroupingSettings groupingSettings)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var validImplicitGuids = new Dictionary<GUID, ImplicitParam>();
            var atlases = new List<SpriteAtlasParam>();

            foreach (var fileToBundle in writeData.FileToBundle)
            {
                if (writeData.FileToObjects.TryGetValue(fileToBundle.Key, out var objects))
                {
                    // NOTE: 参照が全てくるので同一ファイルから複数の参照がくる
                    foreach (var objectId in objects)
                    {
                        var guid = objectId.guid;

                        // EntryされてるExplicit Assetなら無視
                        if (writeData.AssetToFiles.ContainsKey(guid))
                            continue;

                        // Group検索
                        var bundle = fileToBundle.Value;
                        // NOTE: Built-in Shadersはグループが見つからない
                        if (!bundleToAssetGroup.TryGetValue(bundle, out var groupGuid))
                            continue;
                        var path = AssetDatabase.GUIDToAssetPath(guid);

                        // Resourcesがエントリされている場合は警告するが許容する
                        // NOTE: 多くのプロジェクトでTextMeshProが利用されるがTextMeshProがResources前提で設計されるので許容せざるを得ない
                        if (!AddrUtility.IsPathValidForEntry(path))
                            continue;
                        if (path.Contains("/Resources/"))
                        {
                            var selectedGroup = settings.FindGroup(g => g.Guid == groupGuid);
                            Debug.LogWarning($"Resources is duplicated. - {path} / Group : {selectedGroup.name}");
                        }

                        // Lightmapはシーンに依存するので無視
                        if (path.Contains("Lightmap-"))
                            continue;

                        // NOTE: PostProcessingVolumeやPlayableなどインスタンスがないアセットが存在
                        var instance = ObjectIdentifier.ToObject(objectId);
                        var type = instance != null ? instance.GetType() : null;

                        // NOTE: Materialはファイルサイズが小さいので重複を許容して過剰なbundleを避ける
                        if (groupingSettings.allowDuplicatedMaterial)
                        {
                            if (type == typeof(Material))
                                continue;
                        }

                        var isSubAsset = instance != null && AssetDatabase.IsSubAsset(instance);
                        var isResident = groupGuid == groupingSettings.residentGroupGUID;

                        if (validImplicitGuids.TryGetValue(guid, out var param))
                        {
                            if (type != null && !param.usedType.Contains(type))
                                param.usedType.Add(type);
                            if (!param.bundles.Contains(bundle))
                                param.bundles.Add(bundle);
                            param.isSubAsset &= isSubAsset;
                            param.isResident |= isResident;
                        }
                        else
                        {
                            // // Textureは圧縮フォーマットでサイズが著しく変わるので対応する
                            // // NOTE: AssetBundleのLZ4圧縮後の結果は流石に内容物によって変わるのでビルド前チェックは無理
                            // var fullPath = "";
                            // if (path.Contains("Packages/"))
                            //     fullPath = System.IO.Path.GetFullPath(path);
                            // else
                            //     fullPath = System.IO.Path.Combine(Application.dataPath.Replace("/Assets", ""),
                            //         path);
                            // var fileSize = 0L;
                            // if (instance is Texture)
                            //     fileSize = this.GetStorageMemorySizeLong(instance as Texture);
                            // if (fileSize == 0L)
                            //     fileSize = new System.IO.FileInfo(fullPath).Length;

                            param = new ImplicitParam()
                            {
                                guid = guid.ToString(),
                                path = path,
                                isSubAsset = isSubAsset,
                                isResident = isResident,
                                usedType = new List<System.Type>() { type },
                                bundles = new List<string>() { bundle },
                                //fileSize = fileSize,
                            };
                            validImplicitGuids.Add(guid, param);

                            // SpriteAtlasは単品チェックでバラのテクスチャが引っかからないように集めておく
                            if (type == typeof(SpriteAtlas))
                            {
                                var atlasParam = new SpriteAtlasParam()
                                {
                                    isResident = isResident,
                                    instance = instance as SpriteAtlas,
                                };
                                atlases.Add(atlasParam);
                            }
                        }

                        // 確認用
                        //Debug.Log($"{implicitPath} / Entry : {explicitPath} / Group : {selectedGroup.name}");
                    }
                }
            }

            var implicitParams = new List<ImplicitParam>(validImplicitGuids.Values);
            return (implicitParams, atlases);
        }
    }
}
