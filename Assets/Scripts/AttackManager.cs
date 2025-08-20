using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.XR.Hands;
using TMPro;
using UnityEngine.UI;
using System.Diagnostics;
using System.Globalization;

internal class AttackManager : ExperimentManager
{
    #region Constants
    private const long PracticeFeedbackIntervalMs = 2000;
    #endregion

    #region Enums
    private enum ExperimentStage
    {
        Setup,
        Animation,
        Practice,
        Attack,
        Finish
    }
    #endregion

    #region Fields
    private ExperimentStage currentStage;
    string queryDir;
    private uint currentPracticeAttempt, currentAttackRound;
    private GestureAnimation gestureAnimation;
    private readonly Stopwatch stageTimer = new Stopwatch(), feedbackTimer = new Stopwatch();
    private bool isStageActive, isFeedbackActive, isAnimationActive;
    #endregion

    #region Serialized Fields
    [SerializeField] private LineRenderer animationTrail;
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private bool fullyInformed;
    [SerializeField] private uint practiceRounds;
    [SerializeField] private uint attackRounds;
    [SerializeField] private ExperimentStage continueFromStage;
    #endregion

    #region Unity Methods
    protected void Update()
    {
        if (isFeedbackActive)
            UpdateFeedback();

        if (Input.GetKeyDown(KeyCode.Space))
            ResetCoordinateFrame();

        if (currentStage == ExperimentStage.Animation)
        {
            if (Input.GetKeyDown(KeyCode.D))
            {
                gestureAnimation.StopAnimation();
                TransitionToPractice();
            }
            else
            {
                gestureAnimation.UpdateAnimation();
            }
        }       
    }
    #endregion

    #region Initialization
    protected override string GetExperimentType() => "Attack";

    protected override void SetupNewExperiment()
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        logPath = Path.Combine(experimentPath, $"{timestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", timestamp);
        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);

        if (!Directory.Exists(experimentPath))
            Directory.CreateDirectory(experimentPath);

        currentStage = ExperimentStage.Setup;

        TransitionToSetup();
    }

    protected override void LoadPreviousExperiment()
    {
        string timestamp;

        if (continueFromStage == ExperimentStage.Practice) timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        else timestamp = FindLatestTimestamp(Directory.GetFiles(experimentPath, "*_log.txt"), @"(\d+)_log.txt");

        logPath = Path.Combine(experimentPath, $"{timestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", timestamp);
        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);

        if (!Directory.Exists(experimentPath))
            Directory.CreateDirectory(experimentPath);

        currentStage = ExperimentStage.Setup;

        TransitionToSetup();
    }

    private string FindLatestTimestamp(string[] paths, string pattern)
    {
        var timestamps = paths.Select(path => Regex.Match(Path.GetFileName(path), pattern))
                              .Where(match => match.Success)
                              .Select(match => match.Groups[1].Value)
                              .ToList();
        if (!timestamps.Any()) throw new ArgumentException("No matching timestamps found.");
        return timestamps.Max();
    }
    #endregion

    #region Stage Transitions
    protected override void TransitionToNextStage()
    {
        if (continueExperiment)
            TransitionToCheckpointStage();
        else if (fullyInformed)
            TransitionToAnimation();
        else
            TransitionToPractice();
    }

    private void TransitionToCheckpointStage()
    {
        switch (continueFromStage)
        {
            case ExperimentStage.Animation:
                TransitionToAnimation();
                break;
            case ExperimentStage.Practice:
                TransitionToPractice();
                break;
            default:
                TransitionToAttack();
                break;
        }
    }

    private void TransitionToAnimation()
    {
        currentStage = ExperimentStage.Animation;

        SetupAnimationUI();
        SetupAnimationStage();
    }

    private void TransitionToPractice()
    {
        currentStage = ExperimentStage.Practice;

        SetupPracticeUI();
        SetupPracticeStage();
    }

    private void TransitionToAttack()
    {
        currentStage = ExperimentStage.Attack;

        if (isFeedbackActive && feedbackTimer.ElapsedMilliseconds < PracticeFeedbackIntervalMs) return;

        SetupAttackUI();
        SetupAttackStage();
    }

    private void TransitionToFinish()
    {
        currentStage = ExperimentStage.Finish;

        SetupFinishUI();
        SetupFinishStage();
    }
    #endregion

    #region Stage Updates
    private void UpdateFeedback()
    {
        if (feedbackTimer.ElapsedMilliseconds >= PracticeFeedbackIntervalMs)
        {
            isFeedbackActive = false;

            TransitionToAttack();
        }
    }
    #endregion

    #region Stage Helpers

    private void SetupAnimationStage()
    {
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        
        string attackDir = Path.Combine(Path.Combine(Directory.GetParent(Application.dataPath).ToString(), "AttackGesture"));
        string gestureDir = Directory.GetDirectories(attackDir).FirstOrDefault();

        gestureAnimation = new GestureAnimation(gestureDir, new TrailHandler(animationTrail), jointPrefab, interactionRegion);

        gestureAnimation.StartAnimation();
    }

    private void SetupPracticeStage()
    {
        if (currentPracticeAttempt >= practiceRounds)
        {
            stageTimer.Stop();
            isStageActive = false;

            Log($"Practice Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

            SetInformationUI(center: true);
            infoTextCenter.text = "Now, you will start the attack";

            isFeedbackActive = true;
            feedbackTimer.Restart();

            TransitionToAttack();

            return;
        }

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_practice_{currentPracticeAttempt}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        Log($"Practice attempt {currentPracticeAttempt + 1}/{practiceRounds}", currentStage.ToString());

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(() =>
        {
            currentPracticeAttempt++;
            TransitionToPractice();
        });

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupAttackStage()
    {
        if (currentAttackRound >= attackRounds)
        {
            stageTimer.Stop();
            isStageActive = false;

            Log($"Attack Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

            TransitionToFinish();

            return;
        }

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_attack_{currentAttackRound}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        Log($"Attack round {currentAttackRound + 1}/{attackRounds}", currentStage.ToString());

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(() =>
        {
            currentAttackRound++;
            TransitionToAttack();
        });

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupFinishStage()
    {
        currentStage = ExperimentStage.Finish;

        SetupFinishUI();
    }

    private void SetupAnimationUI()
    {
        SetInformationUI(center: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Observer the victim's gesture";
        gestureHandler.Clear();
    }
    private void SetupPracticeUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Practice {currentPracticeAttempt}/{practiceRounds}";
        infoTextCenterBottom.text = "Draw gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
    }
    private void SetupAttackUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Attack {currentAttackRound}/{attackRounds}";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
    }
    private void SetupFinishUI()
    {
        SetInformationUI(centerBottom: true);
        infoTextCenterBottom.text = "This part of the experiment is over\nPut your headset away";

        gestureHandler.Clear();
    }
    #endregion

    #region Helpers
    private void ConfigureGestureCallbacks(Action onFail, Action onPass = null)
    {
        gestureHandler.OnGestureFailed = onFail;
        gestureHandler.OnGesturePassed = onPass;

        gestureHandler.OnGestureStarted = () =>
        {
            Log("Started drawing a gesture", currentStage.ToString());

            if (!isStageActive)
            {
                stageTimer.Restart();
                isStageActive = true;
            }
        };
        gestureHandler.OnGestureFinished = () =>
        {
            Log("Finished drawing a gesture", currentStage.ToString());

            SaveQueryRecord();
        };
    }

    private void SaveQueryRecord()
    {
        gestureHandler.Save(queryDir);

        Log($"Draw Time: {gestureHandler.DrawTimeMs}ms", currentStage.ToString());

        gestureHandler.OnGestureFailed?.Invoke();
    }
    #endregion
}

internal sealed class GestureAnimation
{
    private const float LoopPauseSeconds = 1f;

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

    public GestureAnimation(string gestureDir, TrailHandler animTrailHandler, GameObject jointPrefab, GameObject interactionRegion)
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
}