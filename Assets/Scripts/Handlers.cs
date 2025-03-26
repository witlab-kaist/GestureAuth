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
    private readonly Stopwatch timer1 = new Stopwatch();
    private readonly Stopwatch timer2 = new Stopwatch();
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
    private const float PinchEntryThreshold = 0.9f;
    private const float PinchDistanceThreshold = 0.02f;
    private const float PinchDisableDistanceThreshold = 0.04f;
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

    private readonly Stopwatch drawTimer = new Stopwatch();
    private readonly Stopwatch distanceTimer = new Stopwatch();
    private readonly Stopwatch regionTimer = new Stopwatch();
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
        float pinchStrength = MetaAimHand.right.pinchStrengthIndex.magnitude;
        bool pinchEntered = !isGestureActive && pinchStrength >= PinchEntryThreshold;

        if (pinchEntered)
        {
            if (TryRecordPosition(subsystem))
            {
                visualFeedbackHandler.ShowFeedback();
                OnGestureStarted?.Invoke();
                drawTimer.Restart();
                isGestureActive = true;
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
    private const float SmoothingFactor = 32f; //16f;
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
        lastLocalGesturePos = localGesturePos;
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
    private const float SimplificationTolerance = 0.0001f;
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