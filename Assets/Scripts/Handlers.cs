using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.Hands;
using System.Diagnostics;
using Unity.XR.CoreUtils;
using System.Threading.Tasks;
using UnityEngine.XR;
using System.Globalization;

internal class InteractionHandler
{
    #region Constants
    private const long InteractionTimeThresholdMs = 500;
    private const float AlphaSpeed = 0.25f;
    private const float ExpansionSpeed = 0.02f;
    #endregion

    #region Properties
    public Action PromptUser { get; set; }
    public Action OnCube1Selected { get; set; }
    public Action OnCube2Selected { get; set; }
    #endregion

    #region Fields
    private readonly GameObject cube1, cube2;
    private readonly Stopwatch timer1 = new Stopwatch(), timer2 = new Stopwatch();
    private bool isColliding1, isColliding2;
    private readonly Vector3 originalScale;
    private readonly Color originalColor;
    #endregion

    #region Constructor
    public InteractionHandler(GameObject cube1, GameObject cube2)
    {
        this.cube1 = cube1;
        this.cube2 = cube2;
        originalColor = cube1.GetComponent<Renderer>().material.color;
        originalScale = cube1.transform.localScale;
    }
    #endregion

    #region Methods
    public void OnUpdatedHandsPromptUser(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType type)
    {
        PromptUser?.Invoke();

        if (OnCube1Selected != null && TryRecordInteraction(cube1, timer1, ref isColliding1))
        {
            subsystem.updatedHands -= OnUpdatedHandsPromptUser;
            OnCube1Selected();
        }
        if (OnCube2Selected != null && TryRecordInteraction(cube2, timer2, ref isColliding2))
        {
            subsystem.updatedHands -= OnUpdatedHandsPromptUser;
            OnCube2Selected();
        }
    }

    private bool TryRecordInteraction(GameObject cube, Stopwatch timer, ref bool isColliding)
    {
        bool colliding = cube.GetComponent<CollisionDetection>().CollidingObjects > 0;
        if (colliding)
        {
            if (!isColliding)
            {
                isColliding = true;
                ResetCube(cube);
                timer.Restart();
            }

            var renderer = cube.GetComponent<Renderer>();
            renderer.material.color = new Color(originalColor.r, originalColor.g, originalColor.b, renderer.material.color.a + Time.deltaTime * AlphaSpeed);
            cube.transform.localScale += Vector3.one * Time.deltaTime * ExpansionSpeed;

            if (timer.ElapsedMilliseconds >= InteractionTimeThresholdMs)
            {
                isColliding = false;
                return true;
            }
        }
        else if (isColliding)
        {
            isColliding = false;
            ResetCube(cube);
        }
        return false;
    }

    private void ResetCube(GameObject cube)
    {
        cube.GetComponent<Renderer>().material.color = originalColor;
        cube.transform.localScale = originalScale;
    }
    #endregion
}

internal class GestureHandler
{
    #region Constants
    private const float PinchEntryDistanceThreshold = 0.01f;
    private const float PinchDistanceThreshold = 0.02f;
    private const float PinchDisableDistanceThreshold = 0.03f;
    private const long PinchIgnoreTimeThresholdMs = 500;

    private const float GestureIgnoreLengthThreshold = 0.1f;
    
    private const float SimplificationTolerance = 0.13f;
    private const long DistanceToleranceMs = 1500;
    private const long RegionToleranceMs = 1500;
    #endregion

    #region Properties
    public long DrawTimeMs => drawTimer.ElapsedMilliseconds;
    public Action OnGestureStarted { get; set; }
    public Action OnGestureFinished { get; set; }
    public Action OnGesturePassed { get; set; }
    public Action OnGestureFailed { get; set; }
    #endregion

    #region Fields
    private readonly string experimentPath;

    private readonly Transform regionTransform;
    private readonly Bounds regionBounds = new Bounds(Vector3.zero, Vector3.one);

    private readonly TrailHandler gestureTrail;
    private readonly VisualFeedbackHandler visualFeedbackHandler;

    private readonly Transform mainCameraTransform;
    private readonly List<Vector3> headsetPositions = new List<Vector3>();
    private readonly List<Quaternion> headsetRotations = new List<Quaternion>();
    private readonly List<Vector3> headsetVelocities = new List<Vector3>();
    private readonly List<Vector3> headsetAngularVelocities = new List<Vector3>();

    private readonly List<Vector3> gesture = new List<Vector3>();
    private readonly List<long> timestamps = new List<long>();
    private bool isGestureActive;

    private readonly HashSet<XRHandJointID> validJoints = new HashSet<XRHandJointID>();
    private readonly List<List<Vector3>> jointPositions;
    private readonly List<List<Quaternion>> jointRotations;
    private readonly List<float> jointRadii;

    private readonly Stopwatch drawTimer = new Stopwatch(), distanceTimer = new Stopwatch(), regionTimer = new Stopwatch();
    private bool isOutOfDistance, isOutOfRegion;
    #endregion

    #region Constructor
    public GestureHandler(string experimentPath, Transform mainCameraTransform, Transform regionTransform, TrailHandler gestureTrail, TrailHandler xTrail,
        TrailHandler yTrail, TrailHandler zTrail, GameObject leftMark, GameObject rightMark, GameObject topMark, GameObject baseMark, GameObject frontMark,
        GameObject feedbackSphere)
    {
        this.experimentPath = experimentPath;
        this.mainCameraTransform = mainCameraTransform;
        this.regionTransform = regionTransform;
        this.gestureTrail = gestureTrail;

        visualFeedbackHandler = new VisualFeedbackHandler(feedbackSphere, leftMark, rightMark, topMark, baseMark, frontMark, gestureTrail,
            xTrail, yTrail, zTrail, regionTransform);

        int jointCount = Enum.GetNames(typeof(XRHandJointID)).Length;
        jointPositions = Enumerable.Range(0, jointCount).Select(_ => new List<Vector3>()).ToList();
        jointRotations = Enumerable.Range(0, jointCount).Select(_ => new List<Quaternion>()).ToList();
        jointRadii = Enumerable.Repeat(0f, jointCount).ToList();

        foreach (XRHandJointID joint in Enum.GetValues(typeof(XRHandJointID)).Cast<XRHandJointID>())
            if (joint != XRHandJointID.Invalid && joint != XRHandJointID.EndMarker)
                validJoints.Add(joint);
    }
    #endregion

    #region Data Recording
    public void OnUpdatedHandsRecord(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType type)
    {
        if (!isGestureActive)
        {
            if (TryGetPinchDistance(subsystem, out float pinchDistance) && pinchDistance <= PinchEntryDistanceThreshold)
            {
                if (TryRecordPosition(subsystem))
                {
                    visualFeedbackHandler.ShowFeedback();
                    OnGestureStarted?.Invoke();
                    drawTimer.Restart();
                    isGestureActive = true;
                }
            }
        }

        if (isGestureActive && !TryRecordPosition(subsystem))
        {
            drawTimer.Stop();
            visualFeedbackHandler.HideFeedback();
            isGestureActive = isOutOfDistance = isOutOfRegion = false;

            if (drawTimer.ElapsedMilliseconds < PinchIgnoreTimeThresholdMs || Length() < GestureIgnoreLengthThreshold)
            {
                Clear();
                return;
            }

            subsystem.updatedHands -= OnUpdatedHandsRecord;
            OnGestureFinished?.Invoke();
        }
    }

    private bool TryRecordPosition(XRHandSubsystem subsystem)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var indexTip = subsystem.rightHand.GetJoint(XRHandJointID.IndexTip);
        var thumbTip = subsystem.rightHand.GetJoint(XRHandJointID.ThumbTip);

        if (!indexTip.TryGetPose(out Pose indexPose) || !thumbTip.TryGetPose(out Pose thumbPose))
            return false;

        float distance = (indexPose.position - thumbPose.position).magnitude;
        if (distance > PinchDisableDistanceThreshold || (isOutOfDistance && distanceTimer.ElapsedMilliseconds > DistanceToleranceMs) ||
            (isOutOfRegion && regionTimer.ElapsedMilliseconds > RegionToleranceMs))
            return false;

        if (distance > PinchDistanceThreshold)
        {
            if (!isOutOfDistance)
            {
                isOutOfDistance = true;
                distanceTimer.Restart();
            }
        }
        else
        {
            isOutOfDistance = false;
        }

        Vector3 worldGesturePos = (indexPose.position + thumbPose.position) / 2f;
        Vector3 localGesturePos = regionTransform.InverseTransformPoint(worldGesturePos);

        Vector3 localHeadsetPos = regionTransform.InverseTransformPoint(mainCameraTransform.position);
        Quaternion localHeadsetRot = Quaternion.Inverse(regionTransform.rotation) * mainCameraTransform.rotation;

        if (!regionBounds.Contains(localGesturePos))
        {
            if (!isOutOfRegion)
            {
                isOutOfRegion = true;
                regionTimer.Restart();
            }
        }
        else
        {
            isOutOfRegion = false;
        }

        gesture.Add(localGesturePos);
        timestamps.Add(timestamp);

        headsetPositions.Add(localHeadsetPos);
        headsetRotations.Add(localHeadsetRot);

        InputDevice headset = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (headset.TryGetFeatureValue(CommonUsages.deviceVelocity, out Vector3 velocity))
        {
            headsetVelocities.Add(velocity);
        }
        if (headset.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out Vector3 angularVelocity))
        {
            headsetAngularVelocities.Add(angularVelocity);
        }

        RecordJoints(subsystem);

        visualFeedbackHandler.UpdateFeedback(localGesturePos, isOutOfDistance, isOutOfRegion);

        return true;
    }

    private void RecordJoints(XRHandSubsystem subsystem)
    {
        foreach (var joint in validJoints)
        {
            int idx = joint.ToIndex();
            var j = subsystem.rightHand.GetJoint(joint);
            if (j.TryGetPose(out Pose pose))
            {
                Pose worldPose = regionTransform.InverseTransformPose(pose);
                jointPositions[idx].Add(worldPose.position);
                jointRotations[idx].Add(worldPose.rotation);
            }
            if (j.TryGetRadius(out float radius))
                jointRadii[idx] = radius;
        }
    }
    #endregion

    #region Data Saving
    public void Save(string dir)
    {
        SaveGestureCoordinates(dir);

        List<long> timestampsCopy = new List<long>(timestamps);
        Dictionary<XRHandJointID, List<Vector3>> jointPositionsCopy = validJoints.ToDictionary(
            j => j,
            j => new List<Vector3>(jointPositions[j.ToIndex()])
        );
        Dictionary<XRHandJointID, List<Quaternion>> jointRotationsCopy = validJoints.ToDictionary(
            j => j,
            j => new List<Quaternion>(jointRotations[j.ToIndex()])
        );
        List<float> jointRadiiCopy = new List<float>(jointRadii);
        List<Vector3> headsetPositionsCopy = new List<Vector3>(headsetPositions);
        List<Quaternion> headsetRotationsCopy = new List<Quaternion>(headsetRotations);
        List<Vector3> headsetVelocitiesCopy = new List<Vector3>(headsetVelocities);
        List<Vector3> headsetAngularVelocitiesCopy = new List<Vector3>(headsetAngularVelocities);

        Task.Run(() => SaveTimestamps(dir, timestampsCopy));
        Task.Run(() => SaveHeadsetData(dir, headsetPositionsCopy, headsetRotationsCopy, headsetVelocitiesCopy, headsetAngularVelocitiesCopy));
        Task.Run(() => SaveJointData(dir, jointPositionsCopy, jointRotationsCopy, jointRadiiCopy));
    }

    private void SaveGestureCoordinates(string dir)
    {
        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "ex.txt")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(gesture.Count);
            foreach (var point in gesture)
            {
                sw.WriteLine("####");
                sw.WriteLine($"{point.x:0.#######},{point.y:0.#######},{point.z:0.#######}");
            }
        }
    }

    private void SaveTimestamps(string dir, List<long> timestampsCopy)
    {
        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "timestamps.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(timestampsCopy.Count);
            foreach (var ts in timestampsCopy)
                sw.WriteLine(ts);
        }
    }

    private void SaveHeadsetData(string dir, List<Vector3> headsetPositionsCopy, List<Quaternion> headsetRotationsCopy,
        List<Vector3> headsetVelocitiesCopy, List<Vector3> headsetAngularVelocitiesCopy)
    {
        Directory.CreateDirectory(Path.Combine(experimentPath, dir, "headset"));

        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "headset", "headset_positions.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(headsetPositionsCopy.Count);
            foreach (var p in headsetPositionsCopy)
                sw.WriteLine($"{p.x:0.#######};{p.y:0.#######};{p.z:0.#######}");
        }

        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "headset", "headset_rotations.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(headsetRotationsCopy.Count);
            foreach (var q in headsetRotationsCopy)
                sw.WriteLine($"{q.x:0.#######};{q.y:0.#######};{q.z:0.#######};{q.w:0.#######}");
        }

        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "headset", "headset_velocities.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(headsetVelocitiesCopy.Count);
            foreach (var a in headsetVelocitiesCopy)
                sw.WriteLine($"{a.x:0.#######};{a.y:0.#######};{a.z:0.#######}");
        }

        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "headset", "headset_angular_velocities.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(headsetAngularVelocitiesCopy.Count);
            foreach (var av in headsetAngularVelocitiesCopy)
                sw.WriteLine($"{av.x:0.#######};{av.y:0.#######};{av.z:0.#######}");
        }
    }

    private void SaveJointData(string dir, Dictionary<XRHandJointID, List<Vector3>> jointPositionsCopy,
        Dictionary<XRHandJointID, List<Quaternion>> jointRotationsCopy, List<float> jointRadiiCopy)
    {
        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "radius.csv")))
        {
            sw.WriteLine(dir);
            foreach (var joint in validJoints)
                sw.WriteLine($"{joint};{jointRadiiCopy[joint.ToIndex()]:0.#######}");
        }

        foreach (var joint in validJoints)
        {
            int idx = joint.ToIndex();

            Directory.CreateDirectory(Path.Combine(experimentPath, dir, "position"));
            using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "position", $"{joint}.csv")))
            {
                sw.WriteLine(dir);
                sw.WriteLine(jointPositionsCopy[joint].Count);
                foreach (var p in jointPositionsCopy[joint])
                    sw.WriteLine($"{p.x:0.#######};{p.y:0.#######};{p.z:0.#######}");
            }

            Directory.CreateDirectory(Path.Combine(experimentPath, dir, "rotation"));
            using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "rotation", $"{joint}.csv")))
            {
                sw.WriteLine(dir);
                sw.WriteLine(jointRotationsCopy[joint].Count);
                foreach (var q in jointRotationsCopy[joint])
                    sw.WriteLine($"{q.x:0.#######};{q.y:0.#######};{q.z:0.#######};{q.w:0.#######}");
            }
        }
    }
    #endregion

    #region Helpers
    private bool TryGetPinchDistance(XRHandSubsystem subsystem, out float distance)
    {
        distance = float.MaxValue;
        var indexTip = subsystem.rightHand.GetJoint(XRHandJointID.IndexTip);
        var thumbTip = subsystem.rightHand.GetJoint(XRHandJointID.ThumbTip);

        if (!indexTip.TryGetPose(out Pose indexPose) || !thumbTip.TryGetPose(out Pose thumbPose))
            return false;

        distance = Vector3.Distance(indexPose.position, thumbPose.position);
        return true;
    }

    public double JackknifeScore(string sampleDir, string queryDir)
    {
        var dataset = new Jackknife.Evaluate.Dataset();
        var blades = new Jackknife.JackknifeBlades();
        Jackknife.Evaluate.Dataset.LoadSubjectDataset(dataset, experimentPath);
        blades.SetIPDefaults();

        var jackknife = new Jackknife.Jackknife(blades);
        foreach (var sample in dataset.SamplesByGesture[dataset.Gestures.IndexOf(sampleDir)])
            jackknife.AddTemplate(sample);

        double score = 1 / jackknife.MatchScore(dataset.SamplesByGesture[dataset.Gestures.IndexOf(queryDir)][0]);
        return score;
    }

    public int SimplificationScore()
    {
        if (gesture.Count == 0) return 0;

        float minX = gesture.Min(p => p.x), minY = gesture.Min(p => p.y), minZ = gesture.Min(p => p.z);
        float maxRange = Mathf.Max(gesture.Max(p => p.x - minX), gesture.Max(p => p.y - minY), gesture.Max(p => p.z - minZ));
        if (maxRange == 0) return gesture.Count;

        var normalized = gesture.Select(p => new Vector3((p.x - minX) / maxRange, (p.y - minY) / maxRange, (p.z - minZ) / maxRange)).ToList();
        var simplified = new List<Vector3>();
        LineUtility.Simplify(normalized, SimplificationTolerance, simplified);
        return simplified.Count;
    }

    public float DimensionScore()
    {
        if (gesture == null || gesture.Count < 3) return 0;
        Vector3 mean = Vector3.zero;
        foreach (var pt in gesture) 
            mean += pt;
        mean /= gesture.Count;
        float xx = 0, xy = 0, xz = 0;
        float yy = 0, yz = 0, zz = 0;
        foreach (var pt in gesture)
        {
            Vector3 d = pt - mean;
            xx += d.x * d.x;
            xy += d.x * d.y;
            xz += d.x * d.z;
            yy += d.y * d.y;
            yz += d.y * d.z;
            zz += d.z * d.z;
        }
        int n = gesture.Count;
        xx /= n; xy /= n; xz /= n;
        yy /= n; yz /= n; zz /= n;
        float m = (xx + yy + zz) / 3f;
        float a00 = xx - m, a11 = yy - m, a22 = zz - m;
        float p2 = a00 * a00 + a11 * a11 + a22 * a22 + 2 * (xy * xy + xz * xz + yz * yz);
        float p = Mathf.Sqrt(p2 / 6f);
        float detB = (a00 * (a11 * a22 - yz * yz) - xy * (xy * a22 - xz * yz) + xz * (xy * yz - a11 * xz)) / (p * p * p);
        detB = Mathf.Clamp(detB, -1f, 1f);
        float theta = Mathf.Acos(detB) / 3f;
        float eig1 = m + 2 * p * Mathf.Cos(theta);
        float eig3 = m + 2 * p * Mathf.Cos(theta + (2 * Mathf.PI / 3f));
        float eig2 = 3 * m - eig1 - eig3;
        float maxEig = Mathf.Max(eig1, Mathf.Max(eig2, eig3));
        float minEig = Mathf.Min(eig1, Mathf.Min(eig2, eig3));
        if (Mathf.Approximately(maxEig, 0f)) return 0;
        float ratio = minEig / maxEig;
        return ratio;
    }

    public float Length()
    {
        float total = 0;
        for (int i = 1; i < gesture.Count; i++)
            total += (gesture[i] - gesture[i - 1]).magnitude;
        return total;
    }

    public void Clear()
    {
        gesture.Clear();
        timestamps.Clear();
        headsetPositions.Clear();
        headsetRotations.Clear();
        headsetVelocities.Clear();
        headsetAngularVelocities.Clear();
        foreach (var joint in validJoints)
        {
            jointPositions[joint.ToIndex()].Clear();
            jointRotations[joint.ToIndex()].Clear();
        }
        gestureTrail.Clear();
    }
    #endregion
}

internal class VisualFeedbackHandler
{
    #region Constants
    private const float SmoothingFactor = 16f; //16f;
    private const float RegionRadius = 0.5f;
    #endregion

    #region Fields
    private readonly GameObject feedbackSphere;
    private readonly GameObject leftMark, rightMark, topMark, baseMark, frontMark;
    private readonly TrailHandler gestureTrail;
    private readonly TrailHandler xTrail, yTrail, zTrail;

    private readonly Renderer feedbackSphereRenderer;
    private readonly MeshRenderer[] markRenderers;

    private Vector3 lastLocalGesturePos;
    #endregion

    #region Constructor
    public VisualFeedbackHandler(GameObject feedbackSphere, GameObject leftMark, GameObject rightMark, GameObject topMark, GameObject baseMark,
        GameObject frontMark, TrailHandler gestureTrail, TrailHandler xTrail, TrailHandler yTrail, TrailHandler zTrail, Transform regionTransform)
    {
        this.feedbackSphere = feedbackSphere;
        feedbackSphereRenderer = feedbackSphere.GetComponent<Renderer>();
        markRenderers = new[]
        {
            leftMark.GetComponent<MeshRenderer>(),
            rightMark.GetComponent<MeshRenderer>(),
            topMark.GetComponent<MeshRenderer>(),
            baseMark.GetComponent<MeshRenderer>(),
            frontMark.GetComponent<MeshRenderer>()
        };
        this.leftMark = leftMark;
        this.rightMark = rightMark;
        this.topMark = topMark;
        this.baseMark = baseMark;
        this.frontMark = frontMark;
        this.gestureTrail = gestureTrail;
        this.xTrail = xTrail;
        this.yTrail = yTrail;
        this.zTrail = zTrail;
    }
    #endregion

    #region Methods
    public void UpdateFeedback(Vector3 localGesturePos, bool isOutOfDistance, bool isOutOfRegion)
    {
        feedbackSphere.transform.localPosition = localGesturePos;
        feedbackSphereRenderer.material.color = (isOutOfDistance || isOutOfRegion) ? Color.red : Color.green;

        leftMark.transform.localPosition = new Vector3(-RegionRadius, localGesturePos.y, localGesturePos.z);
        rightMark.transform.localPosition = new Vector3(RegionRadius, localGesturePos.y, localGesturePos.z);
        topMark.transform.localPosition = new Vector3(localGesturePos.x, RegionRadius, localGesturePos.z);
        baseMark.transform.localPosition = new Vector3(localGesturePos.x, -RegionRadius, localGesturePos.z);
        frontMark.transform.localPosition = new Vector3(localGesturePos.x, localGesturePos.y, RegionRadius);

        xTrail.UpdateTwoPoints(
            new Vector3(Math.Max(localGesturePos.x, RegionRadius), localGesturePos.y, localGesturePos.z),
            new Vector3(Math.Min(localGesturePos.x, -RegionRadius), localGesturePos.y, localGesturePos.z)
        );
        yTrail.UpdateTwoPoints(
            new Vector3(localGesturePos.x, Math.Max(localGesturePos.y, RegionRadius), localGesturePos.z),
            new Vector3(localGesturePos.x, Math.Min(localGesturePos.y, -RegionRadius), localGesturePos.z)
        );
        zTrail.UpdateTwoPoints(
            new Vector3(localGesturePos.x, localGesturePos.y, RegionRadius),
            new Vector3(localGesturePos.x, localGesturePos.y, localGesturePos.z)
        );

        Vector3 smoothedLocalGesturePos = Vector3.Lerp(lastLocalGesturePos != Vector3.zero ? lastLocalGesturePos : localGesturePos, localGesturePos, Time.deltaTime * SmoothingFactor);
        lastLocalGesturePos = smoothedLocalGesturePos;
        gestureTrail.DrawPoint(smoothedLocalGesturePos);
    }

    public void ShowFeedback()
    {
        feedbackSphereRenderer.enabled = true;
        foreach (var renderer in markRenderers)
            renderer.enabled = true;
    }

    public void HideFeedback()
    {
        feedbackSphereRenderer.enabled = false;
        foreach (var renderer in markRenderers)
            renderer.enabled = false;
        lastLocalGesturePos = Vector3.zero;
        xTrail.Clear();
        yTrail.Clear();
        zTrail.Clear();
    }
    #endregion
}
internal class TrailHandler
{
    #region Constants
    private const float SimplificationTolerance = 0.0002f;
    #endregion

    #region Fields
    private readonly LineRenderer lineRenderer;
    #endregion

    #region Constructor
    public TrailHandler(LineRenderer lineRenderer)
    {
        this.lineRenderer = lineRenderer;
    }
    #endregion

    #region Methods
    public void DrawPoint(Vector3 point)
    {
        lineRenderer.positionCount++;
        lineRenderer.SetPosition(lineRenderer.positionCount - 1, point);
        if (lineRenderer.positionCount > 1)
            lineRenderer.Simplify(SimplificationTolerance);
    }

    public void UpdateTwoPoints(Vector3 point1, Vector3 point2)
    {
        if (lineRenderer.positionCount != 2)
            lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, point1);
        lineRenderer.SetPosition(1, point2);
    }

    public void Clear()
    {
        lineRenderer.positionCount = 0;
    }
    #endregion
}

internal class AnimationHandler
{
    #region Constants
    private const float LoopPauseSeconds = 1f;
    #endregion

    #region Fields
    private readonly HashSet<XRHandJointID> validJoints = new HashSet<XRHandJointID>();
    private readonly Dictionary<XRHandJointID, GameObject> jointObjects = new Dictionary<XRHandJointID, GameObject>();

    private readonly List<long> timestamps = new List<long>();
    private readonly List<Vector3> gesture = new List<Vector3>();
    private readonly Dictionary<XRHandJointID, Vector3[]> jointPositions = new();
    private readonly Dictionary<XRHandJointID, Quaternion[]> jointRotations = new();

    private readonly TrailHandler animTrailHandler;
    private readonly GameObject jointPrefab;
    private readonly GameObject interactionRegion;

    private Stopwatch animTimer = new Stopwatch();

    private int frameCount, animFrameIndex;
    private long animT0Ms;
    private float loopPauseRemaining;
    #endregion

    #region Constructor
    public AnimationHandler(string gestureDir, TrailHandler animTrailHandler, GameObject jointPrefab, GameObject interactionRegion)
    {
        this.animTrailHandler = animTrailHandler;
        this.jointPrefab = jointPrefab;
        this.interactionRegion = interactionRegion;

        validJoints.Clear();
        foreach (XRHandJointID joint in Enum.GetValues(typeof(XRHandJointID)).Cast<XRHandJointID>())
            if (joint != XRHandJointID.Invalid && joint != XRHandJointID.EndMarker)
                validJoints.Add(joint);

        LoadTimestamps(gestureDir);
        LoadGesture(gestureDir);
        LoadJoints(gestureDir);

        frameCount = gesture.Count;
        this.interactionRegion = interactionRegion;
    }
    #endregion

    #region Animation Control
    public void StartAnimation()
    {
        InstantiateJointObjects();

        animFrameIndex = 0;
        animT0Ms = timestamps[0];
        animTimer.Restart();
        loopPauseRemaining = 0f;

        ApplyAnimationFrame(0);
    }

    public void UpdateAnimation()
    {
        if (loopPauseRemaining > 0f)
        {
            loopPauseRemaining -= Time.deltaTime;
            if (loopPauseRemaining <= 0f)
            {
                StopAnimation();
                StartAnimation();
            }
            return;
        }

        double elapsedMsScaled = animTimer.Elapsed.TotalMilliseconds;

        while (animFrameIndex + 1 < frameCount)
        {
            long targetMs = timestamps[animFrameIndex + 1] - animT0Ms;
            if (elapsedMsScaled + 0.0001 < targetMs) break;
            animFrameIndex++;
            ApplyAnimationFrame(animFrameIndex);
        }

        if (animFrameIndex >= frameCount - 1)
        {
            loopPauseRemaining = Mathf.Max(0f, LoopPauseSeconds);
        }
    }

    public void StopAnimation()
    {
        animTimer.Reset();
        animTrailHandler.Clear();
        DestroyJointObjects();
    }
    #endregion

    #region Data Loading
    private void LoadTimestamps(string gestureDir)
    {
        var tsPath = Path.Combine(gestureDir, "timestamps.csv");
        var tsLines = File.ReadAllLines(tsPath);
        for (int i = 2; i < tsLines.Length; i++)
        {
            if (long.TryParse(tsLines[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                timestamps.Add(ms);
        }
    }

    private void LoadGesture(string gestureDir)
    {
        var exPath = Path.Combine(gestureDir, "ex.txt");
        var exLines = File.ReadAllLines(exPath);
        for (int i = 2; i + 1 < exLines.Length; i += 2)
        {
            var xyz = exLines[i + 1].Split(',');
            float x = float.Parse(xyz[0], CultureInfo.InvariantCulture);
            float y = float.Parse(xyz[1], CultureInfo.InvariantCulture);
            float z = float.Parse(xyz[2], CultureInfo.InvariantCulture);
            gesture.Add(new Vector3(x, y, z));
        }
    }

    private void LoadJoints(string gestureDir)
    {
        string posDir = Path.Combine(gestureDir, "position");
        string rotDir = Path.Combine(gestureDir, "rotation");

        foreach (var joint in validJoints)
        {
            string pCsv = Path.Combine(posDir, $"{joint}.csv");
            string rCsv = Path.Combine(rotDir, $"{joint}.csv");

            var pLines = File.ReadAllLines(pCsv);
            var rLines = File.ReadAllLines(rCsv);

            int pCount = Convert.ToInt32(pLines[1]);
            int rCount = Convert.ToInt32(rLines[1]);
            int n = Math.Min(pCount, rCount);

            var pArr = new Vector3[n];
            var rArr = new Quaternion[n];

            for (int i = 0; i < n; i++)
            {
                var parts = pLines[i + 2].Split(';');
                pArr[i] = new Vector3(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture)
                );
            }
            for (int i = 0; i < n; i++)
            {
                var parts = rLines[i + 2].Split(';');
                rArr[i] = new Quaternion(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    float.Parse(parts[3], CultureInfo.InvariantCulture)
                );
            }

            jointPositions[joint] = pArr;
            jointRotations[joint] = rArr;
        }
    }
    #endregion

    #region Helpers
    private void ApplyAnimationFrame(int i)
    {
        foreach (var j in validJoints)
        {
            var go = jointObjects[j];
            go.transform.localPosition = jointPositions[j][i];
            go.transform.localRotation = jointRotations[j][i];
        }
        animTrailHandler.DrawPoint(gesture[i]);
    }

    private void InstantiateJointObjects()
    {
        foreach (var j in validJoints)
        {
            var jObj = GameObject.Instantiate(jointPrefab, interactionRegion.transform);
            jObj.name = $"VictimJoint_{j}";
            jObj.SetActive(true);
            jointObjects[j] = jObj;
        }
    }

    private void DestroyJointObjects()
    {
        foreach (var kv in jointObjects)
            if (kv.Value) UnityEngine.Object.Destroy(kv.Value);
        jointObjects.Clear();
    }
    #endregion
}