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
    private const float Gesture3DRatioThreshold = 0.1f;

    private const long TutorialFeedbackIntervalMs = 2000;
    #endregion

    #region Enums
    private enum ExperimentStage
    {
        Setup,
        Tutorial,
        Create,
        Match,
        Practice,
        Distract,
        Recall,
        Collect,
        Finish
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
    private ExperimentStage currentStage;
    private TutorialStage currentTutorialStage;
    private string sampleDir, queryDir;
    private List<string> sampleDirs = new List<string>();
    private uint currentPracticeAttempt, currentRecallAttempt, currentCollectRound;
    private readonly Stopwatch stageTimer = new Stopwatch(), feedbackTimer = new Stopwatch(), distractTimer = new Stopwatch();
    private bool isDistractFinished, isRecallSuccessful, isStageActive, isFeedbackActive;
    #endregion

    #region Serialized Fields
    [SerializeField] private bool enforce3DGesture;
    [SerializeField] private uint practiceRounds;
    [SerializeField] private uint recallAttempts;
    [SerializeField] private uint collectRounds;
    [SerializeField] private long distractTimeSeconds;
    [SerializeField] private ExperimentStage continueFromStage;
    #endregion

    #region Unity Methods
    protected void Update()
    {
        switch (currentStage)
        {
            case ExperimentStage.Distract:
                UpdateDistractStage();
                break;
            default:
                if (isFeedbackActive)
                    UpdateFeedback();
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
        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);

        if (!Directory.Exists(experimentPath))
            CreateExperimentDirectories();

        currentStage = ExperimentStage.Setup;
        currentTutorialStage = TutorialStage.AxisX;

        TransitionToSetup();
    }

    protected override void LoadPreviousExperiment()
    {
        string timestamp;

        if (continueFromStage == ExperimentStage.Create) timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        else timestamp = FindLatestTimestamp(Directory.GetFiles(experimentPath, "*_log.txt"), @"(\d+)_log.txt");

        logPath = Path.Combine(experimentPath, $"{timestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", timestamp);
        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);

        if (!Directory.Exists(experimentPath))
            CreateExperimentDirectories();

        string samplesPath = Path.Combine(experimentPath, "samples.txt");
        if (File.Exists(samplesPath)) sampleDirs = File.ReadAllLines(samplesPath).ToList();

        currentStage = ExperimentStage.Setup;

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
            case ExperimentStage.Create:
                TransitionToCreate();
                break;
            case ExperimentStage.Practice:
                TransitionToPractice();
                break;
            case ExperimentStage.Recall:
                TransitionToRecall();
                break;
            case ExperimentStage.Collect:
                TransitionToCollect();
                break;
            default:
                TransitionToTutorial();
                break;
        }
    }

    private void TransitionToTutorial()
    {
        currentStage = ExperimentStage.Tutorial;
        
        SetupTutorialUI();
        SetupTutorialStage();
    }

    private void TransitionToCreate()
    {
        currentStage = ExperimentStage.Create;
        
        if (isFeedbackActive && feedbackTimer.ElapsedMilliseconds < TutorialFeedbackIntervalMs) return;

        SetupCreateUI();
        SetupCreateStage();
    }

    private void TransitionToMatch()
    {
        currentStage = ExperimentStage.Match;
        
        SetupMatchUI();
        SetupMatchStage();
    }

    private void TransitionToPractice()
    {
        currentStage = ExperimentStage.Practice;

        SetupPracticeUI();
        SetupPracticeStage();
    }

    private void TransitionToDistract()
    {
        currentStage = ExperimentStage.Distract;

        SetInformationUI(centerBottom: true);
        
        distractTimer.Restart();
    }

    private void TransitionToRecall()
    {
        currentStage = ExperimentStage.Recall;
       
        SetupRecallUI();
        SetupRecallStage();
    }

    private void TransitionToCollect()
    {
        currentStage = ExperimentStage.Collect;

        SetupCollectUI();
        SetupCollectStage();
    }

    private void TransitionToFinish()
    {
        currentStage = ExperimentStage.Finish;

        SetupFinishUI();
        SetupFinishStage();
    }
    #endregion

    #region Stage Updates
    private void UpdateDistractStage()
    {
        if (isDistractFinished)
        {
            currentStage = ExperimentStage.Setup;
            continueExperiment = true;

            TransitionToSetup();

            isDistractFinished = false;
            infoTextCenterBottom.text = "";

            return;
        }

        if (distractTimer.ElapsedMilliseconds / 1000 >= distractTimeSeconds)
        {
            isDistractFinished = true;
            
            distractTimer.Stop();

            Log("Distract time is up", currentStage.ToString());
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
    #endregion

    #region Stage Helpers
    private void SetupTutorialStage()
    {
        sampleDir = currentTutorialStage.ToString();
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_tutorial_{currentTutorialStage.ToString().ToLower()}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        var points = LoadGesturePoints("Tutorial", sampleDir);
        PositionTutorialLine(points[0], points[points.Count - 1]);
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        Action onPass = currentTutorialStage switch
        {
            TutorialStage.AxisX => () => { currentTutorialStage = TutorialStage.AxisY; PromptToContinue(TransitionToTutorial); },
            TutorialStage.AxisY => () => { currentTutorialStage = TutorialStage.AxisZ; PromptToContinue(TransitionToTutorial); },
            TutorialStage.AxisZ => () => { currentTutorialStage = TutorialStage.Diagonal; PromptToContinue(TransitionToTutorial); },
            TutorialStage.Diagonal => () =>
            {
                stageTimer.Stop();
                isStageActive = false;

                Log($"Tutorial Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

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

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupCreateStage()
    {
        sampleDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_create";
        Directory.CreateDirectory(Path.Combine(experimentPath, sampleDir));
        sampleDirs.Clear();

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(CreateStageFailed, CreateStagePassed);

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupMatchStage()
    {
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_match";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(MatchStageFailed, () =>
        {
            stageTimer.Stop();
            isStageActive = false;

            Log($"Create Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

            sampleDirs.Add(queryDir);
            SaveSampleDirs();

            TransitionToPractice();
        });

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupPracticeStage()
    {
        if (currentPracticeAttempt >= practiceRounds)
        {
            stageTimer.Stop();
            isStageActive = false;

            Log($"Practice Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

            continueFromStage = ExperimentStage.Recall;

            TransitionToDistract();

            return;
        }

        currentPracticeAttempt++;

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_practice_{currentPracticeAttempt}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        Log($"Practice attempt {currentPracticeAttempt}/{practiceRounds}", currentStage.ToString());

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(PracticeStageFailed, PracticeStagePassed);

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupRecallStage()
    {
        if (currentRecallAttempt >= recallAttempts || isRecallSuccessful)
        {
            stageTimer.Stop();
            isStageActive = false;

            Log($"Recall Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

            continueFromStage = ExperimentStage.Collect;

            TransitionToDistract();

            return;
        }

        currentRecallAttempt++;

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_recall_{currentRecallAttempt}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        Log($"Recall attempt {currentRecallAttempt}/{recallAttempts}", currentStage.ToString());

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(RecallStageFailed, () =>
        {
            isRecallSuccessful = true;

            sampleDirs.Add(queryDir);
            SaveSampleDirs();

            PromptToContinue(TransitionToRecall);

            infoTextCenter.text = $"<color=green>The gestures match!</color>\n\n{infoTextCenter.text}";
        });

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupCollectStage()
    {
        if (currentCollectRound >= collectRounds)
        {
            stageTimer.Stop();
            isStageActive = false;

            Log($"Collect Time: {stageTimer.ElapsedMilliseconds}ms", currentStage.ToString());

            TransitionToFinish();

            return;
        }

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_collect_{currentCollectRound}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));

        Log($"Collect round {currentCollectRound + 1}/{collectRounds}", currentStage.ToString());
        
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        
        ConfigureGestureCallbacks(() =>
        {
            infoTextCenterBottom.text = "<color=red>The gesture doesn't match! Try again.</color>";

            TransitionToCollect();
        }, () => {
            currentCollectRound++;

            infoTextCenterBottom.text = "<color=green>The gesture matches!</color>";
            
            TransitionToCollect();
        });

        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    private void SetupFinishStage()
    {
        currentStage = ExperimentStage.Finish;

        SetupFinishUI();
    }

    private void CreateStagePassed()
    {
        SetupCreatePassedUI();

        PositionObject(interactionCubeLeft, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeLeftRotation, InteractionCubeDistance);
        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);

        interactionHandler.PromptUser = null;
        interactionHandler.OnCube1Selected = TransitionToCreate;
        interactionHandler.OnCube2Selected = () =>
        {
            sampleDirs.Add(sampleDir);
            SaveSampleDirs();

            TransitionToMatch();
        };
        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }
    private void CreateStageFailed() => PromptToContinue(TransitionToCreate, "The gesture is " + (enforce3DGesture ? "either flat or " : "") + "too simple\nPlease, create a different one");
    private void MatchStageFailed()
    {
        SetupMatchFailedUI();

        PositionObject(interactionCubeLeft, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeLeftRotation, InteractionCubeDistance);
        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);

        interactionHandler.PromptUser = null;
        interactionHandler.OnCube1Selected = TransitionToCreate;
        interactionHandler.OnCube2Selected = TransitionToMatch;

        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }
    private void TutorialStageFailed() => PromptToContinue(TransitionToTutorial, "<color=red>The gesture doesn't match the example!</color>");
    private void PracticeStagePassed() => PromptToContinue(TransitionToPractice, "<color=green>The gestures match!</color>");
    private void PracticeStageFailed() => PromptToContinue(TransitionToPractice, "<color=red>The gestures don't match!</color>");
    private void RecallStageFailed() => PromptToContinue(TransitionToRecall, "<color=red>The gestures don't match!</color>");

    private void SetupTutorialUI()
    {
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Pinch with your <b>right</b> hand to start a gesture\nRelease the pinch to finish a gesture\nConnect two dots starting from the <color=red>RED</color> dot";
        infoPicture.sprite = rightHandPinchSprite;
        
        EnableTutorialElements(true);
    }
    private void SetupCreateUI()
    {
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Create a gesture\nDraw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;
        
        isFeedbackActive = false;
        gestureHandler.Clear();
    }
    private void SetupMatchUI()
    {
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Match the gesture\nDraw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
    }
    private void SetupPracticeUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Practice {currentPracticeAttempt}/{practiceRounds}";
        infoTextCenterBottom.text = "Draw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
    }
    private void SetupRecallUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Recall {currentRecallAttempt}/{recallAttempts}";
        infoTextCenterBottom.text = "Draw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
    }
    private void SetupCollectUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Collect {currentCollectRound}/{collectRounds}";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
    }
    private void SetupFinishUI()
    {
        SetInformationUI(centerBottom: true);
        infoTextCenterBottom.text = "The first part of the experiment is over\nPut your headset away";

        gestureHandler.Clear();
    }
    private void SetupCreatePassedUI()
    {
        SetInformationUI(center: true, left: true, right: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "Confirm the gesture";
        infoTextLeft.text = "Put your <b>right</b> hand into the <b>LEFT</b> cube\nand wait to <color=red>create a new gesture</color>";
        infoTextRight.text = "Put your <b>right</b> hand into the <b>RIGHT</b> cube\nand wait to <color=green>confirm the gesture</color>";
        infoPicture.sprite = rightHandOpenSprite;
    }
    private void SetupMatchFailedUI()
    {
        SetInformationUI(center: true, left: true, right: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "<color=red>The gestures don't match</color>";
        infoTextLeft.text = "Put your <b>right</b> hand into the <b>LEFT</b> cube\nand wait to create a new gesture";
        infoTextRight.text = "Put your <b>right</b> hand into the <b>RIGHT</b> cube\nand wait to try matching again";
        infoPicture.sprite = rightHandOpenSprite;

        gestureHandler.Clear();
    }
    private void SetupPromptUI(string feedback)
    {
        SetInformationUI(center: true, picture: true);
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = $"{feedback}{(string.IsNullOrEmpty(feedback) ? "" : "\n\n")}Put your <b>right</b> hand into the cube and wait";
        infoPicture.sprite = rightHandOpenSprite;

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

            if (currentStage == ExperimentStage.Create) SaveSampleRecord();
            else SaveQueryRecord();
        };
    }

    private void PromptToContinue(Action nextStage, string feedback = "")
    {
        currentStage = ExperimentStage.Setup;

        SetupPromptUI(feedback);

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

    private void SaveSampleDirs()
    {
        string samplesPath = Path.Combine(experimentPath, "samples.txt");
        File.WriteAllLines(samplesPath, sampleDirs);
    }

    private void SaveSampleRecord()
    {
        gestureHandler.Save(sampleDir);

        float length = gestureHandler.Length();
        int simplificationScore = gestureHandler.SimplificationScore();

        Log($"Gesture length: {length}", currentStage.ToString());
        Log($"Gesture simplification score: {simplificationScore}", currentStage.ToString());

        bool isValid = length >= GestureLengthThreshold && simplificationScore >= GestureSimplificationScoreThreshold;

        if (enforce3DGesture)
        {
            float dimensionScore = gestureHandler.DimensionScore();

            Log($"Gesture dimension score: {dimensionScore}", currentStage.ToString());

            isValid = isValid && dimensionScore >= Gesture3DRatioThreshold;
        }

        Log($"Draw Time ({(isValid ? "Valid" : "Invalid")}): {gestureHandler.DrawTimeMs}ms", currentStage.ToString());

        if (isValid) gestureHandler.OnGesturePassed?.Invoke();
        else gestureHandler.OnGestureFailed?.Invoke();
    }

    private void SaveQueryRecord()
    {
        gestureHandler.Save(queryDir);

        double matchScore;
        
        if (currentStage == ExperimentStage.Tutorial) matchScore = gestureHandler.JackknifeScore(sampleDir, queryDir);
        else matchScore = sampleDirs.Max(sample => gestureHandler.JackknifeScore(sample, queryDir));

        Log($"Gesture match score: {matchScore:0.#######}", currentStage.ToString());

        bool isMatch = matchScore >= GestureScoreThreshold;

        Log($"Draw Time ({(isMatch ? "Passed" : "Failed")}): {gestureHandler.DrawTimeMs}ms", currentStage.ToString());

        if (isMatch) gestureHandler.OnGesturePassed?.Invoke();
        else gestureHandler.OnGestureFailed?.Invoke();
    }
    #endregion
}