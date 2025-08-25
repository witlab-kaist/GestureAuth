using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using System.Diagnostics;
using System;

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
    private AnimationHandler animationHandler;
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
                animationHandler.StopAnimation();
                TransitionToPractice();
            }
            else
            {
                animationHandler.UpdateAnimation();
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

        animationHandler = new AnimationHandler(gestureDir, new TrailHandler(animationTrail), jointPrefab, interactionRegion);

        animationHandler.StartAnimation();
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