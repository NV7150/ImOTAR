using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class TreeSpawner : MonoBehaviour {
    // Trigger
    [SerializeField] private bool onAwake = false;

    // Refs
    [SerializeField] private Transform center;          // spawn center
    [SerializeField] private GameObject[] prefabs;      // tree prefabs

    // Placement
    [SerializeField] private int count = 10;            // number of trees
    [SerializeField] private float radius = 2.0f;       // scatter radius (meters)
    [SerializeField] private float spacing = 0.5f;      // minimal spacing between instances (meters)
    [SerializeField] private Vector2 scaleRange = new Vector2(0.9f, 1.2f); // uniform scale [min, max]
    [SerializeField] private bool randomYaw = true;     // randomize yaw around Y axis
    [SerializeField] private bool randomY = false;      // randomize Y position by range
    [SerializeField] private Vector2 yRange = new Vector2(0f, 0f); // Y offset range [min, max]
    [SerializeField] private Vector3 baseEuler = Vector3.zero; // base rotation offset (degrees)
    [SerializeField] private int maxTries = 1000;       // rejection sampling limit

    // Constants
    private const float FullTurnDeg = 360f;             // full rotation degrees (no magic numbers)
    private const float TwoPi = Mathf.PI * 2f;          // 2*pi for polar sampling

    private readonly List<Transform> spawned = new List<Transform>();

    private void Awake(){
        if (onAwake) Spawn();
    }

    // Public trigger: place trees using current settings
    public void Spawn(){
        Validate();

        var accepted = new List<Vector3>(count);
        int tries = 0;

        while (accepted.Count < count && tries < maxTries){
            tries++;

            // Sample uniformly inside a circle on XZ plane at center.y
            Vector2 p2 = SampleInCircle(radius);
            float yOffset = randomY ? UnityEngine.Random.Range(yRange.x, yRange.y) : 0f;
            var candidate = new Vector3(center.position.x + p2.x, center.position.y + yOffset, center.position.z + p2.y);

            if (!IsFarEnough(candidate, accepted, spacing)) continue;
            accepted.Add(candidate);
        }

        if (accepted.Count < count)
            throw new InvalidOperationException("TreeSpawner: placement failed (increase radius or reduce spacing).");

        var baseRot = Quaternion.Euler(baseEuler);
        for (int i = 0; i < accepted.Count; i++){
            var prefab = prefabs[UnityEngine.Random.Range(0, prefabs.Length)];
            if (prefab == null) throw new NullReferenceException("TreeSpawner: prefabs contains null.");

            float yaw = randomYaw ? UnityEngine.Random.Range(0f, FullTurnDeg) : 0f;
            var yawRot = Quaternion.Euler(0f, yaw, 0f);
            var rot = yawRot * baseRot; // world-Y yaw, then model's base correction

            float s = UnityEngine.Random.Range(scaleRange.x, scaleRange.y);
            var inst = Instantiate(prefab, accepted[i], rot).transform;
            inst.localScale = new Vector3(s, s, s);
            inst.SetParent(transform, true);

            spawned.Add(inst);
        }
    }

    // Public trigger: remove all spawned instances
    public void Clear(){
        for (int i = 0; i < spawned.Count; i++){
            if (spawned[i] != null) Destroy(spawned[i].gameObject);
        }
        spawned.Clear();
    }

    private void Validate(){
        if (center == null) throw new NullReferenceException("TreeSpawner: center not assigned.");
        if (prefabs == null || prefabs.Length == 0) throw new NullReferenceException("TreeSpawner: prefabs not assigned.");
        for (int i = 0; i < prefabs.Length; i++) if (prefabs[i] == null) throw new NullReferenceException("TreeSpawner: prefabs contains null.");
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "TreeSpawner: count must be > 0.");
        if (radius < 0f) throw new ArgumentOutOfRangeException(nameof(radius), "TreeSpawner: radius must be >= 0.");
        if (spacing < 0f) throw new ArgumentOutOfRangeException(nameof(spacing), "TreeSpawner: spacing must be >= 0.");
        if (scaleRange.x <= 0f || scaleRange.y <= 0f) throw new ArgumentOutOfRangeException(nameof(scaleRange), "TreeSpawner: scale must be > 0.");
        if (scaleRange.x > scaleRange.y) throw new ArgumentException("TreeSpawner: scaleRange min must be <= max.");
        if (randomY && yRange.x > yRange.y) throw new ArgumentException("TreeSpawner: yRange min must be <= max.");
        if (maxTries <= 0) throw new ArgumentOutOfRangeException(nameof(maxTries), "TreeSpawner: maxTries must be > 0.");
    }

    private static Vector2 SampleInCircle(float r){
        // Uniform random point inside circle using polar coordinates
        float u = UnityEngine.Random.value;   // angle factor
        float v = UnityEngine.Random.value;   // radius factor
        float t = TwoPi * u;
        float rr = r * Mathf.Sqrt(v);
        return new Vector2(rr * Mathf.Cos(t), rr * Mathf.Sin(t));
    }

    private static bool IsFarEnough(Vector3 p, List<Vector3> pts, float minDist){
        float minSqr = minDist * minDist;
        for (int i = 0; i < pts.Count; i++){
            if ((pts[i] - p).sqrMagnitude < minSqr) return false;
        }
        return true;
    }
}


