using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

[ExecuteAlways]
public class HeatTestDriver : MonoBehaviour
{
    // 這個驅動器負責把場景中的「測試元素」批次寫入模擬：
    // - 多個熱源 / 冷源
    // - 多個障礙物
    // - 多個局部速度場
    // 並可選擇用 UI Toggle 同步啟用狀態。

    [Serializable]
    public class SphereTarget
    {
        public enum TargetShape
        {
            Sphere,
            Box
        }

        [Tooltip("此目標項目是否啟用。")]
        public bool enabled = true;
        [Tooltip("優先作為中心位置與旋轉來源的錨點。")]
        public Transform anchor;
        [Tooltip("未指定錨點時使用的世界座標位置。")]
        public Vector3 worldPosition;
        [Tooltip("此項目使用球形或方盒形區域。")]
        public TargetShape shape = TargetShape.Sphere;
        //[Min(0.01f)] public float radius = 0.8f;
        [Tooltip("球形區域的半徑。")]
        [Range(0.01f, 10f)] public float radius = 0.8f;
        [Tooltip("方盒形區域的世界尺寸。")]
        public Vector3 boxSize = new Vector3(1f, 1f, 1f);
        [Tooltip("可用來同步啟用狀態的 UI Toggle。")]
        public Toggle enabledToggle;
        [Tooltip("是否反轉 Toggle 的開關語意。")]
        public bool invertToggleValue;
        [Tooltip("切換啟用狀態時，是否連同錨點物件本身一起開關。")]
        public bool controlAnchorGameObject = true;
        [Tooltip("切換啟用狀態時要一起開關的其他物件。")]
        public List<GameObject> controlledGameObjects = new List<GameObject>();

        public Vector3 GetWorldCenter()
        {
            // 有指定錨點時優先跟隨錨點，否則使用固定世界座標。
            return anchor != null ? anchor.position : worldPosition;
        }

        public Quaternion GetWorldRotation()
        {
            return anchor != null ? anchor.rotation : Quaternion.identity;
        }

        public Vector3 GetWorldBoxSize()
        {
            return new Vector3(
                Mathf.Max(Mathf.Abs(boxSize.x), 0.01f),
                Mathf.Max(Mathf.Abs(boxSize.y), 0.01f),
                Mathf.Max(Mathf.Abs(boxSize.z), 0.01f));
        }

        public bool HasValidShape()
        {
            if (shape == TargetShape.Sphere)
                return radius > 0f;

            Vector3 size = GetWorldBoxSize();
            return size.x > 0f && size.y > 0f && size.z > 0f;
        }

        public float GetPlumeReferenceRadius()
        {
            if (shape == TargetShape.Sphere)
                return Mathf.Max(radius, 0.01f);

            Vector3 size = GetWorldBoxSize();
            float volume = Mathf.Max(size.x * size.y * size.z, 1e-6f);
            return Mathf.Pow((3f * volume) / (4f * Mathf.PI), 1f / 3f);
        }

        public void SyncEnabledFromToggle()
        {
            if (enabledToggle == null)
                return;

            // invertToggleValue = true 時，切換語意反向（On 代表停用）。
            enabled = invertToggleValue ? !enabledToggle.isOn : enabledToggle.isOn;
        }

        public void SyncControlledGameObjects()
        {
            if (controlAnchorGameObject && anchor != null)
                SetGameObjectActive(anchor.gameObject, enabled);

            for (int i = 0; i < controlledGameObjects.Count; i++)
            {
                GameObject targetObject = controlledGameObjects[i];
                SetGameObjectActive(targetObject, enabled);
            }
        }

        private static void SetGameObjectActive(GameObject targetObject, bool active)
        {
            if (targetObject == null)
                return;

            if (targetObject.activeSelf != active)
                targetObject.SetActive(active);
        }
    }

    [Serializable]
    public class HeatSourceEntry
    {
        [Tooltip("熱源作用的目標區域設定。")]
        public SphereTarget target = new SphereTarget();
        [FormerlySerializedAs("strength")]
        [Tooltip("熱源輸入功率，單位為瓦特。")]
        public float powerWatts = 1200f;
    }

    [Serializable]
    public class ColdSourceEntry
    {
        [Tooltip("冷源作用的目標區域設定。")]
        public SphereTarget target = new SphereTarget();
        [FormerlySerializedAs("coolingStrength")]
        [Tooltip("冷源移熱功率，單位為瓦特。")]
        public float coolingPowerWatts = 3500f;
    }

    [Serializable]
    public class ObstacleEntry
    {
        [Tooltip("障礙物作用的目標區域設定。")]
        public SphereTarget target = new SphereTarget();
        [Tooltip("啟用 Collider 模式時，拿來體素化的障礙物碰撞器。")]
        public Collider obstacleCollider;
        [Tooltip("是否改用 Collider 形狀，而不是球形目標。")]
        public bool useColliderShape;
        [Tooltip("Collider 轉 obstacle 時額外外擴的表面厚度。")]
        [Min(0f)] public float colliderSurfacePadding = 0.02f;
        [Tooltip("MeshCollider 是否必須為 Convex 才允許使用。")]
        public bool requireConvexMeshCollider;
        [Tooltip("障礙物強度，1 代表完全阻擋。")]
        [Range(0f, 1f)] public float value = 1f;

        [NonSerialized] private bool _cached;
        [NonSerialized] private bool _cachedEnabled;
        [NonSerialized] private bool _cachedUseColliderShape;
        [NonSerialized] private float _cachedValue;
        [NonSerialized] private float _cachedRadius;
        [NonSerialized] private Vector3 _cachedCenter;
        [NonSerialized] private int _cachedColliderId;
        [NonSerialized] private Vector3 _cachedColliderPosition;
        [NonSerialized] private Quaternion _cachedColliderRotation;
        [NonSerialized] private Vector3 _cachedColliderScale;
        [NonSerialized] private float _cachedColliderSurfacePadding;
        [NonSerialized] private bool _cachedRequireConvexMeshCollider;
        [NonSerialized] private bool _cachedColliderEnabled;
        [NonSerialized] private int _cachedMeshId;
        [NonSerialized] private Vector3 _cachedShapeDataA;
        [NonSerialized] private Vector3 _cachedShapeDataB;

        public bool HasStateChanged()
        {
            bool enabledState = target != null && target.enabled;
            Vector3 center = useColliderShape ? Vector3.zero : (target != null ? target.GetWorldCenter() : Vector3.zero);
            float radius = useColliderShape ? 0f : (target != null ? target.radius : 0f);

            int colliderId = obstacleCollider != null ? obstacleCollider.GetInstanceID() : 0;
            bool colliderEnabled = obstacleCollider != null && obstacleCollider.enabled && obstacleCollider.gameObject.activeInHierarchy;
            Vector3 colliderPosition = obstacleCollider != null ? obstacleCollider.transform.position : Vector3.zero;
            Quaternion colliderRotation = obstacleCollider != null ? obstacleCollider.transform.rotation : Quaternion.identity;
            Vector3 colliderScale = obstacleCollider != null ? obstacleCollider.transform.lossyScale : Vector3.one;
            int meshId = obstacleCollider is MeshCollider meshCollider && meshCollider.sharedMesh != null
                ? meshCollider.sharedMesh.GetInstanceID()
                : 0;

            GetColliderShapeSignature(obstacleCollider, out Vector3 shapeDataA, out Vector3 shapeDataB);

            if (!_cached)
                return true;

            return _cachedEnabled != enabledState ||
                   _cachedUseColliderShape != useColliderShape ||
                   !Mathf.Approximately(_cachedValue, value) ||
                   !Mathf.Approximately(_cachedRadius, radius) ||
                   _cachedCenter != center ||
                   _cachedColliderId != colliderId ||
                   _cachedColliderEnabled != colliderEnabled ||
                   _cachedColliderPosition != colliderPosition ||
                   _cachedColliderRotation != colliderRotation ||
                   _cachedColliderScale != colliderScale ||
                   !Mathf.Approximately(_cachedColliderSurfacePadding, colliderSurfacePadding) ||
                   _cachedRequireConvexMeshCollider != requireConvexMeshCollider ||
                   _cachedMeshId != meshId ||
                   _cachedShapeDataA != shapeDataA ||
                   _cachedShapeDataB != shapeDataB;
        }

        public void CacheState()
        {
            _cached = true;
            _cachedEnabled = target != null && target.enabled;
            _cachedUseColliderShape = useColliderShape;
            _cachedValue = value;
            _cachedRadius = useColliderShape ? 0f : (target != null ? target.radius : 0f);
            _cachedCenter = useColliderShape ? Vector3.zero : (target != null ? target.GetWorldCenter() : Vector3.zero);
            _cachedColliderId = obstacleCollider != null ? obstacleCollider.GetInstanceID() : 0;
            _cachedColliderEnabled = obstacleCollider != null && obstacleCollider.enabled && obstacleCollider.gameObject.activeInHierarchy;
            _cachedColliderPosition = obstacleCollider != null ? obstacleCollider.transform.position : Vector3.zero;
            _cachedColliderRotation = obstacleCollider != null ? obstacleCollider.transform.rotation : Quaternion.identity;
            _cachedColliderScale = obstacleCollider != null ? obstacleCollider.transform.lossyScale : Vector3.one;
            _cachedColliderSurfacePadding = colliderSurfacePadding;
            _cachedRequireConvexMeshCollider = requireConvexMeshCollider;
            _cachedMeshId = obstacleCollider is MeshCollider meshCollider && meshCollider.sharedMesh != null
                ? meshCollider.sharedMesh.GetInstanceID()
                : 0;
            GetColliderShapeSignature(obstacleCollider, out _cachedShapeDataA, out _cachedShapeDataB);
        }

        public void InvalidateCache()
        {
            _cached = false;
        }

        private static void GetColliderShapeSignature(Collider collider, out Vector3 a, out Vector3 b)
        {
            a = Vector3.zero;
            b = Vector3.zero;

            if (collider == null)
                return;

            switch (collider)
            {
                case BoxCollider box:
                    a = box.center;
                    b = box.size;
                    break;
                case SphereCollider sphere:
                    a = sphere.center;
                    b = new Vector3(sphere.radius, 0f, 0f);
                    break;
                case CapsuleCollider capsule:
                    a = capsule.center;
                    b = new Vector3(capsule.radius, capsule.height, capsule.direction);
                    break;
                case MeshCollider mesh:
                    a = mesh.convex ? Vector3.one : Vector3.zero;
                    b = mesh.sharedMesh != null ? mesh.sharedMesh.bounds.size : Vector3.zero;
                    break;
            }
        }
    }

    [Serializable]
    public class VelocityEntry
    {
        [Tooltip("局部風場作用的目標區域設定。")]
        public SphereTarget target = new SphereTarget();
        [Tooltip("要寫入此區域的速度向量。")]
        public Vector3 velocity = new Vector3(1.5f, 0f, 0f);
        [Tooltip("是否在原本速度場上累加，而不是直接覆寫。")]
        public bool additive;
    }

    [Header("References")]
    [Tooltip("要被這個測試驅動器控制的熱模擬元件。")]
    [SerializeField] private ClassroomHeatSimulation sim;

    [Header("Update")]
    [Tooltip("是否每幀自動重新把設定寫入模擬。")]
    [SerializeField] private bool applyEveryFrame = true;
    [Tooltip("非播放模式下是否也自動套用設定。")]
    [SerializeField] private bool applyInEditMode = false;
    [Tooltip("套用前是否先清空熱源貼圖。")]
    [SerializeField] private bool clearHeatSourceBeforeApply = true;
    [Tooltip("套用前是否先清空障礙物貼圖。")]
    [SerializeField] private bool clearObstacleBeforeApply = true;
    [Tooltip("套用前是否先把速度場重設成全域背景風。")]
    [SerializeField] private bool resetVelocityBeforeApply = true;

    [Header("Subgrid Thermal Plume Approximation")]
    [Tooltip("當 solver 不直接算浮力時，是否額外注入近似熱羽流速度。")]
    [SerializeField] private bool applyThermalPlumeVelocity = true;
    [Tooltip("估算羽流速度時採樣高度相對於來源尺度的倍率。")]
    [SerializeField, Min(0.25f)] private float plumeSampleHeightMultiplier = 1.5f;
    [Tooltip("羽流影響半徑相對於來源尺度的倍率。")]
    [SerializeField, Min(0.25f)] private float plumeRadiusMultiplier = 1.35f;
    [Tooltip("羽流估算速度的整體倍率。")]
    [SerializeField, Min(0f)] private float plumeVelocityScale = 1.25f;

    [Header("UI Toggle Sync (Optional)")]
    [Tooltip("是否從 UI Toggle 自動同步各項目的 enabled 狀態。")]
    [SerializeField] private bool syncEnabledFromToggles = true;

    [Header("Multi Sources")]
    [Tooltip("場景中所有熱源條目。")]
    [SerializeField] private List<HeatSourceEntry> heatSources = new List<HeatSourceEntry>();
    [Tooltip("場景中所有冷源條目。")]
    [SerializeField] private List<ColdSourceEntry> coldSources = new List<ColdSourceEntry>();
    [Tooltip("場景中所有障礙物條目。")]
    [SerializeField] private List<ObstacleEntry> obstacles = new List<ObstacleEntry>();
    [Tooltip("場景中所有局部速度場條目。")]
    [SerializeField] private List<VelocityEntry> localVelocities = new List<VelocityEntry>();

    [Header("Gizmos")]
    [Tooltip("是否繪製熱源、冷源、障礙物與速度場的 Gizmo。")]
    [SerializeField] private bool drawGizmos = true;
    [Tooltip("速度箭頭 Gizmo 的長度倍率。")]
    [SerializeField] private float velocityGizmoScale = 0.3f;
    [Tooltip("熱源 Gizmo 顏色。")]
    [SerializeField] private Color heatColor = new Color(1f, 0.35f, 0.1f, 0.9f);
    [Tooltip("冷源 Gizmo 顏色。")]
    [SerializeField] private Color coldColor = new Color(0.15f, 0.75f, 1f, 0.9f);
    [Tooltip("障礙物 Gizmo 顏色。")]
    [SerializeField] private Color obstacleColor = new Color(1f, 0.95f, 0.2f, 0.9f);
    [Tooltip("速度場 Gizmo 顏色。")]
    [SerializeField] private Color velocityColor = new Color(0.25f, 1f, 0.35f, 0.9f);

    private ClassroomHeatSimulation _lastObstacleSimulation;
    private int _lastObservedObstacleFieldVersion = -1;
    private int _lastObstacleCount = -1;
    private bool _obstacleFieldBuilt;

    private void Reset()
    {
        if (sim == null)
            sim = FindSimulationInScene();
    }

    private void OnEnable()
    {
        if (sim == null)
            sim = FindSimulationInScene();

        // 啟用時先同步一次 Toggle 狀態，避免第一次 Apply 使用舊值。
        SyncEnabledStatesFromToggles();
        SyncControlledGameObjects();
        InvalidateObstacleCache();
    }

    private void OnValidate()
    {
        SyncEnabledStatesFromToggles();
        SyncControlledGameObjects();
        InvalidateObstacleCache();
    }

    private void Update()
    {
        if (!applyEveryFrame)
            return;

        if (!Application.isPlaying && !applyInEditMode)
            return;

        ApplyNow();
    }

    [ContextMenu("Apply Now")]
    public void ApplyNow()
    {
        if (sim == null)
            sim = FindSimulationInScene();

        SyncEnabledStatesFromToggles();
        SyncControlledGameObjects();

        if (sim == null)
            return;

        // 寫入順序：
        // 1) 依設定清空對應場
        // 2) 寫入熱源/冷源/障礙物/局部速度
        // 這樣每次 Apply 可以得到可預期結果。
        if (clearHeatSourceBeforeApply)
            sim.ClearHeatSource();

        if (resetVelocityBeforeApply)
            sim.ResetVelocityToGlobal();

        ApplyHeatSources();
        ApplyColdSources();
        RebuildObstaclesIfNeeded();
        ApplyLocalVelocities();
    }

    [ContextMenu("Force Obstacle Refresh")]
    public void ForceObstacleRefresh()
    {
        InvalidateObstacleCache();
        if (sim != null)
            RebuildObstaclesIfNeeded(force: true);
    }

    [ContextMenu("Sync Enabled States From Toggles")]
    public void SyncEnabledStatesFromToggles()
    {
        if (!syncEnabledFromToggles)
            return;

        SyncHeatSourceToggles();
        SyncColdSourceToggles();
        SyncObstacleToggles();
        SyncVelocityToggles();
    }

    [ContextMenu("Sync Controlled GameObjects")]
    public void SyncControlledGameObjects()
    {
        SyncHeatSourceGameObjects();
        SyncColdSourceGameObjects();
        SyncObstacleGameObjects();
        SyncVelocityGameObjects();
    }

    private void SyncHeatSourceToggles()
    {
        for (int i = 0; i < heatSources.Count; i++)
        {
            HeatSourceEntry entry = heatSources[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncEnabledFromToggle();
        }
    }

    private void SyncHeatSourceGameObjects()
    {
        for (int i = 0; i < heatSources.Count; i++)
        {
            HeatSourceEntry entry = heatSources[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncControlledGameObjects();
        }
    }

    private void SyncColdSourceToggles()
    {
        for (int i = 0; i < coldSources.Count; i++)
        {
            ColdSourceEntry entry = coldSources[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncEnabledFromToggle();
        }
    }

    private void SyncColdSourceGameObjects()
    {
        for (int i = 0; i < coldSources.Count; i++)
        {
            ColdSourceEntry entry = coldSources[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncControlledGameObjects();
        }
    }

    private void SyncObstacleToggles()
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncEnabledFromToggle();
        }
    }

    private void SyncObstacleGameObjects()
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncControlledGameObjects();
        }
    }

    private void SyncVelocityToggles()
    {
        for (int i = 0; i < localVelocities.Count; i++)
        {
            VelocityEntry entry = localVelocities[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncEnabledFromToggle();
        }
    }

    private void SyncVelocityGameObjects()
    {
        for (int i = 0; i < localVelocities.Count; i++)
        {
            VelocityEntry entry = localVelocities[i];
            if (entry == null || entry.target == null)
                continue;

            entry.target.SyncControlledGameObjects();
        }
    }

    private void ApplyHeatSources()
    {
        bool injectPlumeVelocity = applyThermalPlumeVelocity && sim != null && !sim.UsesSolverBuoyancy;

        for (int i = 0; i < heatSources.Count; i++)
        {
            HeatSourceEntry entry = heatSources[i];
            if (entry == null || entry.target == null || !entry.target.enabled || !entry.target.HasValidShape())
                continue;

            // 以功率（W）換算成體積熱源項，較容易對接實際設備額定功率。
            Vector3 center = entry.target.GetWorldCenter();
            if (entry.target.shape == SphereTarget.TargetShape.Box)
                sim.AddHeatSourcePowerBox(center, entry.target.GetWorldBoxSize(), entry.target.GetWorldRotation(), entry.powerWatts);
            else
                sim.AddHeatSourcePowerSphere(center, entry.target.radius, entry.powerWatts);

            // 若 solver 已經有 buoyancy，就不要再額外塞 plume velocity，避免浮力重複計算。
            if (injectPlumeVelocity && entry.powerWatts > 0f)
            {
                float plumeReferenceRadius = entry.target.GetPlumeReferenceRadius();
                float plumeSpeed = sim.EstimateBuoyantPlumeSpeed(
                    entry.powerWatts,
                    plumeReferenceRadius,
                    plumeSampleHeightMultiplier,
                    plumeVelocityScale);
                if (entry.target.shape == SphereTarget.TargetShape.Box)
                {
                    Vector3 boxSize = entry.target.GetWorldBoxSize();
                    Vector3 plumeSize = new Vector3(
                        boxSize.x * plumeRadiusMultiplier,
                        boxSize.y,
                        boxSize.z * plumeRadiusMultiplier);
                    Vector3 plumeCenter = center + Vector3.up * (boxSize.y * 0.25f);
                    sim.AddVelocityBox(plumeCenter, plumeSize, entry.target.GetWorldRotation(), Vector3.up * plumeSpeed);
                }
                else
                {
                    float plumeRadius = entry.target.radius * plumeRadiusMultiplier;
                    Vector3 plumeCenter = center + Vector3.up * (entry.target.radius * 0.5f);
                    sim.AddVelocitySphere(plumeCenter, plumeRadius, Vector3.up * plumeSpeed);
                }
            }
        }
    }

    private void ApplyColdSources()
    {
        bool injectPlumeVelocity = applyThermalPlumeVelocity && sim != null && !sim.UsesSolverBuoyancy;

        for (int i = 0; i < coldSources.Count; i++)
        {
            ColdSourceEntry entry = coldSources[i];
            if (entry == null || entry.target == null || !entry.target.enabled || !entry.target.HasValidShape())
                continue;

            // 冷源用等效移熱功率（W）表示，Simulation 端會轉成負熱源項。
            Vector3 center = entry.target.GetWorldCenter();
            if (entry.target.shape == SphereTarget.TargetShape.Box)
                sim.AddCoolingPowerBox(center, entry.target.GetWorldBoxSize(), entry.target.GetWorldRotation(), entry.coolingPowerWatts);
            else
                sim.AddCoolingPowerSphere(center, entry.target.radius, entry.coolingPowerWatts);

            // 若 solver 已經有 buoyancy，就不要再額外塞 plume velocity，避免浮力重複計算。
            if (injectPlumeVelocity && entry.coolingPowerWatts > 0f)
            {
                float plumeReferenceRadius = entry.target.GetPlumeReferenceRadius();
                float plumeSpeed = sim.EstimateBuoyantPlumeSpeed(
                    entry.coolingPowerWatts,
                    plumeReferenceRadius,
                    plumeSampleHeightMultiplier,
                    plumeVelocityScale);
                if (entry.target.shape == SphereTarget.TargetShape.Box)
                {
                    Vector3 boxSize = entry.target.GetWorldBoxSize();
                    Vector3 plumeSize = new Vector3(
                        boxSize.x * plumeRadiusMultiplier,
                        boxSize.y,
                        boxSize.z * plumeRadiusMultiplier);
                    Vector3 plumeCenter = center + Vector3.down * (boxSize.y * 0.25f);
                    sim.AddVelocityBox(plumeCenter, plumeSize, entry.target.GetWorldRotation(), Vector3.down * plumeSpeed);
                }
                else
                {
                    float plumeRadius = entry.target.radius * plumeRadiusMultiplier;
                    Vector3 plumeCenter = center + Vector3.down * (entry.target.radius * 0.5f);
                    sim.AddVelocitySphere(plumeCenter, plumeRadius, Vector3.down * plumeSpeed);
                }
            }
        }
    }

    private void RebuildObstaclesIfNeeded(bool force = false)
    {
        if (sim == null)
            return;

        bool mustRebuild = force || clearObstacleBeforeApply || ShouldRebuildObstacles();
        if (!mustRebuild)
            return;

        sim.ClearObstacle();
        ApplyObstacles();
        CacheObstacleStates();
        _lastObstacleSimulation = sim;
        _lastObservedObstacleFieldVersion = sim.ObstacleFieldVersion;
        _lastObstacleCount = obstacles.Count;
        _obstacleFieldBuilt = true;
    }

    private bool ShouldRebuildObstacles()
    {
        if (sim == null)
            return false;

        if (!_obstacleFieldBuilt ||
            !ReferenceEquals(_lastObstacleSimulation, sim) ||
            _lastObservedObstacleFieldVersion != sim.ObstacleFieldVersion ||
            _lastObstacleCount != obstacles.Count)
        {
            return true;
        }

        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null || entry.target == null)
                return true;

            if (entry.HasStateChanged())
                return true;
        }

        return false;
    }

    private void ApplyObstacles()
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null || entry.target == null || !entry.target.enabled)
                continue;

            if (entry.useColliderShape && entry.obstacleCollider != null)
            {
                // 依 Collider 體素化成 obstacle field，較接近實際物件外形。
                sim.SetObstacleCollider(
                    entry.obstacleCollider,
                    entry.value,
                    entry.colliderSurfacePadding,
                    entry.requireConvexMeshCollider);
                continue;
            }

            if (entry.target.radius <= 0f)
                continue;

            // 障礙物採 Set，避免多次加總導致非預期值。
            sim.SetObstacleSphere(entry.target.GetWorldCenter(), entry.target.radius, entry.value);
        }
    }

    private void CacheObstacleStates()
    {
        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null)
                continue;

            entry.CacheState();
        }
    }

    private void InvalidateObstacleCache()
    {
        _lastObstacleSimulation = null;
        _lastObservedObstacleFieldVersion = -1;
        _lastObstacleCount = -1;
        _obstacleFieldBuilt = false;

        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null)
                continue;

            entry.InvalidateCache();
        }
    }

    private void ApplyLocalVelocities()
    {
        for (int i = 0; i < localVelocities.Count; i++)
        {
            VelocityEntry entry = localVelocities[i];
            if (entry == null || entry.target == null || !entry.target.enabled || !entry.target.HasValidShape())
                continue;

            // additive=true：在原速度場上累加；false：覆寫該區域速度。
            Vector3 center = entry.target.GetWorldCenter();
            if (entry.target.shape == SphereTarget.TargetShape.Box)
            {
                if (entry.additive)
                    sim.AddVelocityBox(center, entry.target.GetWorldBoxSize(), entry.target.GetWorldRotation(), entry.velocity);
                else
                    sim.SetVelocityBox(center, entry.target.GetWorldBoxSize(), entry.target.GetWorldRotation(), entry.velocity);
            }
            else if (entry.additive)
            {
                sim.AddVelocitySphere(center, entry.target.radius, entry.velocity);
            }
            else
            {
                sim.SetVelocitySphere(center, entry.target.radius, entry.velocity);
            }
        }
    }

    #region Gizmos 繪製
    private void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;

        DrawHeatGizmos();
        DrawColdGizmos();
        DrawObstacleGizmos();
        DrawVelocityGizmos();
    }

    private void DrawHeatGizmos()
    {
        Gizmos.color = heatColor;
        for (int i = 0; i < heatSources.Count; i++)
        {
            HeatSourceEntry entry = heatSources[i];
            if (entry == null || entry.target == null || !entry.target.enabled || !entry.target.HasValidShape())
                continue;

            DrawTargetWireShape(entry.target);
        }
    }

    private void DrawColdGizmos()
    {
        Gizmos.color = coldColor;
        for (int i = 0; i < coldSources.Count; i++)
        {
            ColdSourceEntry entry = coldSources[i];
            if (entry == null || entry.target == null || !entry.target.enabled || !entry.target.HasValidShape())
                continue;

            DrawTargetWireShape(entry.target);
        }
    }

    private void DrawObstacleGizmos()
    {
        Gizmos.color = obstacleColor;
        for (int i = 0; i < obstacles.Count; i++)
        {
            ObstacleEntry entry = obstacles[i];
            if (entry == null || entry.target == null || !entry.target.enabled)
                continue;

            if (entry.useColliderShape && entry.obstacleCollider != null)
            {
                if (entry.obstacleCollider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                {
                    Matrix4x4 oldMatrix = Gizmos.matrix;
                    Gizmos.matrix = meshCollider.transform.localToWorldMatrix;
                    Gizmos.DrawWireMesh(meshCollider.sharedMesh);
                    Gizmos.matrix = oldMatrix;
                }
                else
                {
                    Bounds bounds = entry.obstacleCollider.bounds;
                    Gizmos.DrawWireCube(bounds.center, bounds.size);
                }
                continue;
            }

            if (entry.target.radius <= 0f)
                continue;

            Gizmos.DrawWireSphere(entry.target.GetWorldCenter(), entry.target.radius);
        }
    }

    #endregion

    #region 公式計算 - 速度箭頭 Gizmo

    private void DrawVelocityGizmos()
    {
        Gizmos.color = velocityColor;
        for (int i = 0; i < localVelocities.Count; i++)
        {
            VelocityEntry entry = localVelocities[i];
            if (entry == null || entry.target == null || !entry.target.enabled || !entry.target.HasValidShape())
                continue;

            // 速度向量箭頭：
            // head = center + velocity * scale
            // 以速度方向法向量與側向量構造箭頭兩翼。
            Vector3 center = entry.target.GetWorldCenter();
            Vector3 dir = entry.velocity;
            float mag = dir.magnitude;

            DrawTargetWireShape(entry.target);

            if (mag <= 1e-5f)
                continue;

            Vector3 head = center + dir * velocityGizmoScale;
            Gizmos.DrawLine(center, head);

            Vector3 n = dir.normalized;
            Vector3 side = Vector3.Cross(n, Vector3.up);
            if (side.sqrMagnitude < 1e-5f)
                side = Vector3.Cross(n, Vector3.right);
            side.Normalize();

            float arrowSize = Mathf.Max(0.02f, 0.12f * mag * velocityGizmoScale);
            Vector3 back = -n * arrowSize;
            Vector3 wing = side * arrowSize * 0.55f;

            Gizmos.DrawLine(head, head + back + wing);
            Gizmos.DrawLine(head, head + back - wing);
        }
    }

    #endregion

    private static void DrawTargetWireShape(SphereTarget target)
    {
        if (target == null)
            return;

        if (target.shape == SphereTarget.TargetShape.Box)
        {
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(target.GetWorldCenter(), target.GetWorldRotation(), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, target.GetWorldBoxSize());
            Gizmos.matrix = oldMatrix;
            return;
        }

        Gizmos.DrawWireSphere(target.GetWorldCenter(), target.radius);
    }

    private static ClassroomHeatSimulation FindSimulationInScene()
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<ClassroomHeatSimulation>();
#else
        return FindObjectOfType<ClassroomHeatSimulation>();
#endif
    }
}
