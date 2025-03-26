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

internal class CreationManager : ExperimentManager
{
    #region Constants
    private const uint GestureSimplificationScoreThreshold = 3;
    private const float GestureLengthThreshold = 0.45f;
    private const long TutorialFeedbackIntervalMs = 2000;
    #endregion

    #region Enums
    private enum ExtendedExperimentStage
    {
        Tutorial = ExperimentStage.Finish + 1,
        Create,
        Match,
        Practice,
        Distract,
        Recall,
        Repeat
    }

    private enum TutorialStage
    {
        AxisX,
        AxisY,
        AxisZ,
        Diagonal
    }
    #endregion

    #region Fields
    private TutorialStage currentTutorialStage;
    private string sampleDir, queryDir;
    private uint currentPracticeAttempt, currentRecallAttempt, currentRepeatAttempt, currentRepeatRound;
    private bool isDistractFinished, isRecallSuccessful;
    private readonly Stopwatch stageTimer = new Stopwatch();
    private readonly Stopwatch feedbackTimer = new Stopwatch();
    private readonly Stopwatch distractTimer = new Stopwatch();
    private bool isStageActive, isFeedbackActive;
    #endregion

    #region Serialized Fields
    [SerializeField] private uint practiceRounds;
    [SerializeField] private uint recallAttempts;
    [SerializeField] private uint repeatRounds;
    [SerializeField] private long distractTimeSeconds;
    [SerializeField] private ExtendedExperimentStage continueFromStage;
    [SerializeField] private bool enforce3DGesture;
    #endregion

    #region Unity Methods
    protected override void Update()
    {
        switch (currentStage)
        {
            case (ExperimentStage)ExtendedExperimentStage.Distract:
                UpdateDistractStage();
                break;
            case ExperimentStage.Uninitialized:
                break;
            default:
                if (isFeedbackActive)
                    UpdateFeedback();
                else if (handSubsystem == null)
                    RestartStageFromInitialization();
                break;
        }

        if (Input.GetKeyDown(KeyCode.Space))
            ResetCoordinateFrame();
    }
    #endregion

    #region Initialization
    protected override string GetExperimentType() => "Creation";

    protected override void SetupNewExperiment()
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        logPath = Path.Combine(experimentPath, $"{timestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", timestamp);

        if (!Directory.Exists(experimentPath))
            CreateExperimentDirectories();

        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);
        currentTutorialStage = TutorialStage.AxisX;
        totalTimer.Start();
        TransitionToSetup();
    }

    protected override void LoadPreviousExperiment()
    {
        string[] logFiles = Directory.GetFiles(experimentPath, "*_log.txt");
        if (logFiles.Length == 0) throw new DirectoryNotFoundException("No log files found.");

        string latestTimestamp = FindLatestTimestamp(logFiles, @"(\d+)_log.txt");
        logPath = Path.Combine(experimentPath, $"{latestTimestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", latestTimestamp);

        string[] createDirs = Directory.GetDirectories(experimentPath, "*_create");
        if (createDirs.Length == 0) throw new DirectoryNotFoundException("No create directories found.");
        sampleDir = $"{FindLatestTimestamp(createDirs, @"(\d+)_create")}_create";

        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);

        TransitionToSetup();
    }

    private void CreateExperimentDirectories()
    {
        Directory.CreateDirectory(experimentPath);
        string tutorialPath = Path.Combine(Directory.GetParent(Application.dataPath).ToString(), "Tutorial");

        foreach (string tutorial in Enum.GetNames(typeof(TutorialStage)))
        {
            string dir = Path.Combine(experimentPath, tutorial);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(tutorialPath, tutorial, "ex.txt"), Path.Combine(dir, "ex.txt"));
        }
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
        else
            TransitionToTutorial();
    }

    private void TransitionToCheckpointStage()
    {
        switch (continueFromStage)
        {
            case ExtendedExperimentStage.Practice:
                TransitionToPractice();
                break;
            case ExtendedExperimentStage.Recall:
                TransitionToRecall();
                break;
            case ExtendedExperimentStage.Repeat:
                TransitionToRepeat();
                break;
            default:
                TransitionToTutorial();
                break;
        }
    }

    private void TransitionToTutorial()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Tutorial;
        SetupTutorialUI();

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        SetupTutorialStep();
        
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToCreate()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Create;
        if (isFeedbackActive && feedbackTimer.ElapsedMilliseconds < TutorialFeedbackIntervalMs)
            return;

        isFeedbackActive = false;
        gestureHandler.Clear();
        SetupCreateUI();

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        sampleDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_create";
        Directory.CreateDirectory(Path.Combine(experimentPath, sampleDir));

        ConfigureGestureCallbacks(CreateStageFailed, CreateStagePassed);
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToMatch()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Match;
        gestureHandler.Clear();
        SetupMatchUI();

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_match";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        ConfigureGestureCallbacks(MatchStageFailed, () =>
        {
            stageTimer.Stop();
            isStageActive = false;
            Log($"Create Time: {stageTimer.ElapsedMilliseconds}ms");
            TransitionToPractice();
        });
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToPractice()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Practice;
        gestureHandler.Clear();
        SetInformationUI();

        if (currentPracticeAttempt >= practiceRounds)
        {
            stageTimer.Stop();
            isStageActive = false;
            Log($"Practice Time: {stageTimer.ElapsedMilliseconds}ms");
            TransitionToDistract();
            return;
        }

        Log($"Practice attempt {currentPracticeAttempt}/{practiceRounds}");
        SetupPracticeUI();
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_practice_{currentPracticeAttempt}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));
        currentPracticeAttempt++;
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(PracticeStageFailed, PracticeStagePassed);
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToDistract()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Distract;
        distractTimer.Start();
        SetInformationUI(centerBottom: true);
    }

    private void TransitionToRecall()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Recall;
        gestureHandler.Clear();
        SetInformationUI();

        if (currentRecallAttempt >= recallAttempts || isRecallSuccessful)
        {
            stageTimer.Stop();
            isStageActive = false;
            Log($"Recall Time: {stageTimer.ElapsedMilliseconds}ms");
            Log($"Recall Fails: {(isRecallSuccessful ? currentRecallAttempt - 1 : recallAttempts)}");
            TransitionToRepeat();
            return;
        }

        Log($"Recall attempt {currentRecallAttempt}/{recallAttempts}");
        SetupRecallUI();
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_recall_{currentRecallAttempt}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));
        currentRecallAttempt++;
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(RecallStageFailed, () =>
        {
            isRecallSuccessful = true;
            PromptToContinue(TransitionToRecall);
            infoTextCenter.text = $"<color=green>The gestures match!</color>\n\n{infoTextCenter.text}";
        });
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToRepeat()
    {
        currentStage = (ExperimentStage)ExtendedExperimentStage.Repeat;
        gestureHandler.Clear();
        SetInformationUI();

        if (currentRepeatRound >= repeatRounds)
        {
            stageTimer.Stop();
            isStageActive = false;
            Log($"Repeat Time: {stageTimer.ElapsedMilliseconds}ms");
            TransitionToFinish();
            return;
        }

        currentRepeatAttempt++;
        Log($"Repeat round {currentRepeatRound + 1}/{repeatRounds}");
        SetupRepeatUI();
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_repeat_{currentRepeatRound}_{currentRepeatAttempt}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        
        ConfigureGestureCallbacks(() =>
        {
            infoTextCenterBottom.text = "<color=red>The gesture doesn't match! Try again.</color>";
            TransitionToRepeat();
        }, () => {
            currentRepeatRound++;
            infoTextCenterBottom.text = "<color=green>The gesture matches!</color>";
            TransitionToRepeat();
        });
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToFinish()
    {
        currentStage = ExperimentStage.Finish;
        gestureHandler.Clear();
        SetInformationUI(centerBottom: true);
        infoTextCenterBottom.text = "The experiment is over. Thank you for participation!";
        totalTimer.Stop();
        Log($"Total Time: {totalTimer.ElapsedMilliseconds / 1000}s");
    }

    #endregion

    #region Stage Updates
    private void UpdateDistractStage()
    {
        if (isDistractFinished)
        {
            currentStage = ExperimentStage.Uninitialized;
            SetInformationUI(center: true, picture: true);
            interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
            infoTextCenter.text = "Put your <b>right</b> hand into the cube and wait to continue";
            infoPicture.sprite = rightHandOpenSprite;

            interactionHandler.PromptUser = () =>
            {
                PositionObject(informationBoard, mainCameraTransform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
                PositionObject(interactionCubeLeft, mainCameraTransform, InteractionCubePositionOffset, InteractionCubePositionOffset, Quaternion.identity, InteractionCubeDistance);
            };
            interactionHandler.OnCube1Selected = () =>
            {
                RecordCurrentTransform();
                TransitionToRecall();
            };
            interactionHandler.OnCube2Selected = null;
            InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
            return;
        }

        if (distractTimer.ElapsedMilliseconds / 1000 >= distractTimeSeconds)
        {
            distractTimer.Stop();
            Log("Distract time is up");
            isDistractFinished = true;
        }
        else
        {
            long secondsLeft = distractTimeSeconds - distractTimer.ElapsedMilliseconds / 1000;
            infoTextCenterBottom.text = $"Put your headset away and wait\n{secondsLeft / 60:00}:{secondsLeft % 60:00}";
        }
    }

    private void UpdateFeedback()
    {
        if (feedbackTimer.ElapsedMilliseconds >= TutorialFeedbackIntervalMs)
        {
            isFeedbackActive = false;
            TransitionToCreate();
        }
    }

    private void RestartStageFromInitialization()
    {
        if (currentStage == ExperimentStage.Setup)
            TransitionToSetup();
    }
    #endregion

    #region Stage Helpers
    private void SetupTutorialStep()
    {
        sampleDir = currentTutorialStage.ToString();
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_tutorial_{currentTutorialStage.ToString().ToLower()}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        var points = LoadGesturePoints(sampleDir);
        PositionTutorialLine(points[0], points[points.Count - 1]);

        Action onPass = currentTutorialStage switch
        {
            TutorialStage.AxisX => () => { currentTutorialStage = TutorialStage.AxisY; PromptToContinue(TransitionToTutorial); },
            TutorialStage.AxisY => () => { currentTutorialStage = TutorialStage.AxisZ; PromptToContinue(TransitionToTutorial); },
            TutorialStage.AxisZ => () => { currentTutorialStage = TutorialStage.Diagonal; PromptToContinue(TransitionToTutorial); },
            TutorialStage.Diagonal => () =>
            {
                stageTimer.Stop();
                isStageActive = false;
                Log($"Tutorial Time: {stageTimer.ElapsedMilliseconds}ms");
                PromptToContinue(() =>
                {
                    SetInformationUI(center: true);
                    infoTextCenter.text = "Now, you will create your own gesture";
                    isFeedbackActive = true;
                    feedbackTimer.Restart();
                    TransitionToCreate();
                });
            },
            _ => throw new InvalidOperationException("Unknown tutorial stage")
        };

        ConfigureGestureCallbacks(TutorialStageFailed, () =>
        {
            onPass();
            infoTextCenter.text = $"<color=green>The gesture matches the example!</color>\n\n{infoTextCenter.text}";
        });
    }

    private void CreateStagePassed()
    {
        SetInformationUI(center: true, left: true, right: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "Confirm the gesture";
        infoTextLeft.text = "Put your <b>right</b> hand into the <b>LEFT</b> cube\nand wait to <color=red>create a new gesture</color>";
        infoTextRight.text = "Put your <b>right</b> hand into the <b>RIGHT</b> cube\nand wait to <color=green>confirm the gesture</color>";
        infoPicture.sprite = rightHandOpenSprite;

        PositionObject(interactionCubeLeft, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeLeftRotation, InteractionCubeDistance);
        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);

        interactionHandler.PromptUser = null;
        interactionHandler.OnCube1Selected = () => { Log("Gesture cancelled"); TransitionToCreate(); };
        interactionHandler.OnCube2Selected = TransitionToMatch;
        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }

    private void MatchStageFailed()
    {
        gestureHandler.Clear();
        SetInformationUI(center: true, left: true, right: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "<color=red>The gestures don't match</color>";
        infoTextLeft.text = "Put your <b>right</b> hand into the <b>LEFT</b> cube\nand wait to create a new gesture";
        infoTextRight.text = "Put your <b>right</b> hand into the <b>RIGHT</b> cube\nand wait to try matching again";
        infoPicture.sprite = rightHandOpenSprite;

        PositionObject(interactionCubeLeft, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeLeftRotation, InteractionCubeDistance);
        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);

        interactionHandler.PromptUser = null;
        interactionHandler.OnCube1Selected = TransitionToCreate;
        interactionHandler.OnCube2Selected = TransitionToMatch;
        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }

    private void TutorialStageFailed() => PromptToContinue(TransitionToTutorial, "<color=red>The gesture doesn't match the example!</color>");
    private void CreateStageFailed() => PromptToContinue(TransitionToCreate, "The gesture is too simple\nPlease, try again");
    private void PracticeStageFailed() => PromptToContinue(TransitionToPractice, "<color=red>The gestures don't match!</color>");
    private void PracticeStagePassed() => PromptToContinue(TransitionToPractice, "<color=green>The gestures match!</color>");
    private void RecallStageFailed() => PromptToContinue(TransitionToRecall, "<color=red>The gestures don't match!</color>");

    private void SetupTutorialUI()
    {
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        EnableTutorialElements(true);
        infoTextCenter.text = "Pinch with your <b>right</b> hand to start a gesture\nRelease the pinch to finish a gesture\nConnect two dots starting from the <color=red>RED</color> dot";
        infoPicture.sprite = rightHandPinchSprite;
    }
    private void SetupCreateUI()
    {
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Create a gesture\nDraw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;
    }
    private void SetupMatchUI()
    {
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Match the gesture\nDraw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;
    }
    private void SetupPracticeUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Practice {currentPracticeAttempt}/{practiceRounds}";
        infoTextCenterBottom.text = "Draw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;
    }
    private void SetupRecallUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Recall Attempt {currentRecallAttempt}/{recallAttempts}";
        infoTextCenterBottom.text = "Draw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;
    }
    private void SetupRepeatUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Repeat {currentRepeatRound}/{repeatRounds}";
        infoPicture.sprite = rightHandPinchSprite;
    }
    #endregion

    #region Helpers
    private void ConfigureGestureCallbacks(Action onFail, Action onPass = null)
    {
        gestureHandler.OnGestureStarted = () =>
        {
            if (!isStageActive)
            {
                stageTimer.Restart();
                isStageActive = true;
            }
            Log("Started drawing a gesture");
        };
        gestureHandler.OnGestureFinished = () =>
        {
            Log("Finished drawing a gesture");
            if (currentStage == (ExperimentStage)ExtendedExperimentStage.Create)
                SaveSampleRecord();
            else
                SaveQueryRecord();
        };
        gestureHandler.OnGestureFailed = onFail;
        gestureHandler.OnGesturePassed = onPass;
    }

    private void PromptToContinue(Action nextStage, string feedback = "")
    {
        currentStage = ExperimentStage.Uninitialized;
        gestureHandler.Clear();
        SetInformationUI(center: true, picture: true);
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = $"{feedback}{(string.IsNullOrEmpty(feedback) ? "" : "\n\n")}Put your <b>right</b> hand into the cube and wait";
        infoPicture.sprite = rightHandOpenSprite;

        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);
        interactionHandler.PromptUser = null;
        interactionHandler.OnCube1Selected = null;
        interactionHandler.OnCube2Selected = nextStage;
        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }

    private void PositionTutorialLine(Vector3 start, Vector3 end)
    {
        startPoint.transform.localPosition = start;
        endPoint.transform.localPosition = end;
        tutorialLine.SetPosition(0, start);
        tutorialLine.SetPosition(1, end);
    }

    private void SaveSampleRecord()
    {
        gestureHandler.Save(sampleDir);
        Log($"Gesture saved to {Path.Combine(experimentPath, sampleDir, "ex.txt")}");
        Log($"Timestamps saved to {Path.Combine(experimentPath, sampleDir, "timestamps.txt")}");

        int simplificationScore = gestureHandler.SimplificationScore();
        float length = gestureHandler.Length();
        Log($"Gesture simplification score: {simplificationScore}");
        Log($"Gesture length: {length}");

        bool isValid = simplificationScore >= GestureSimplificationScoreThreshold && length >= GestureLengthThreshold;

        if (enforce3DGesture)
        {
            bool is3D = gestureHandler.IsGesture3D();
            Log($"Gesture 3D check: {(is3D ? "3D" : "Not 3D")}");
            isValid = isValid && is3D;
        }

        Log($"Draw Time ({(isValid ? "Valid" : "Invalid")}): {gestureHandler.DrawTimeMs}ms");
        (isValid ? gestureHandler.OnGesturePassed : gestureHandler.OnGestureFailed)?.Invoke();
    }

    private void SaveQueryRecord()
    {
        gestureHandler.Save(queryDir);
        Log($"Gesture saved to {Path.Combine(experimentPath, queryDir, "ex.txt")}");
        Log($"Timestamps saved to {Path.Combine(experimentPath, queryDir, "timestamps.txt")}");

        double matchScore = gestureHandler.JackknifeScore(sampleDir, queryDir);
        Log($"Gesture match score: {matchScore:0.#######}");
        bool isMatch = matchScore >= GestureScoreThreshold;
        Log($"Draw Time ({(isMatch ? "Passed" : "Failed")}): {gestureHandler.DrawTimeMs}ms");
        (isMatch ? gestureHandler.OnGesturePassed : gestureHandler.OnGestureFailed)?.Invoke();
    }

    #endregion
}