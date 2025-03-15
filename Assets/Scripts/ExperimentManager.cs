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
using Unity.XR.CoreUtils;
using System.Threading.Tasks;

public class ExperimentManager : MonoBehaviour
{
    #region Constants
    private const double GestureScoreThreshold = 0.25f;
    private const uint GestureSimplificationScoreThreshold = 3;
    private const float GestureLengthThreshold = 0.45f;
    private const long TutorialFeedbackIntervalMs = 2000;
    private const float BoardDistance = 4f;
    private const float InteractionCubeDistance = 0.4f;
    private const float InteractionRegionDistance = 0.2f;

    private static readonly Vector3 BoardPositionOffset = new Vector3(0, 1f, 0);
    private static readonly Vector3 InteractionCubePositionOffset = new Vector3(0, -0.195f, 0);
    private static readonly Vector3 InteractionRegionPositionOffset = new Vector3(0, -0.195f, 0);
    private static readonly Quaternion InteractionCubeLeftRotation = Quaternion.Euler(0, -40, 0);
    private static readonly Quaternion InteractionCubeRightRotation = Quaternion.Euler(0, 40, 0);
    #endregion

    #region Enums
    private enum ExperimentStage
    {
        Uninitialized,
        Setup,
        Tutorial,
        Create,
        Match,
        Practice,
        Distract,
        Recall,
        Finish,
        RecallSetup
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
    private string experimentPath, logPath;
    private string sampleDir, queryDir;

    private XRHandSubsystem handSubsystem;
    private GestureHandler gestureHandler;
    private InteractionHandler interactionHandler;

    private GameObject localCoordinateFrame;
    private uint currentPracticeAttempt, currentRecallAttempt;
    private bool isDistractFinished, isRecallSuccessful;

    private readonly Stopwatch totalTimer = new Stopwatch();
    private readonly Stopwatch stageTimer = new Stopwatch();
    private readonly Stopwatch feedbackTimer = new Stopwatch();
    private readonly Stopwatch distractTimer = new Stopwatch();
    private bool isStageActive, isFeedbackActive;
    #endregion

    #region Serialized Fields
    [SerializeField] private string subjectCode;
    [SerializeField] private bool continueExperiment;
    [SerializeField] private uint practiceRounds;
    [SerializeField] private uint recallAttempts;
    [SerializeField] private long distractTimeSeconds;

    [SerializeField] private LineRenderer gestureTrail;
    [SerializeField] private LineRenderer xLine;
    [SerializeField] private LineRenderer yLine;
    [SerializeField] private LineRenderer zLine;
    [SerializeField] private GameObject leftMark;
    [SerializeField] private GameObject rightMark;
    [SerializeField] private GameObject topMark;
    [SerializeField] private GameObject baseMark;
    [SerializeField] private GameObject frontMark;
    [SerializeField] private GameObject feedbackSphere;
    [SerializeField] private Transform mainCameraTransform;
    [SerializeField] private GameObject interactionCubeLeft;
    [SerializeField] private GameObject interactionCubeRight;
    [SerializeField] private GameObject interactionRegion;
    [SerializeField] private GameObject informationBoard;
    [SerializeField] private LineRenderer tutorialLine;
    [SerializeField] private GameObject startPoint;
    [SerializeField] private GameObject endPoint;

    [SerializeField] private TextMeshProUGUI infoTextCenter;
    [SerializeField] private TextMeshProUGUI infoTextCenterTop;
    [SerializeField] private TextMeshProUGUI infoTextCenterBottom;
    [SerializeField] private TextMeshProUGUI infoTextLeft;
    [SerializeField] private TextMeshProUGUI infoTextRight;
    [SerializeField] private Image infoPicture;
    [SerializeField] private Sprite rightHandPinchSprite;
    [SerializeField] private Sprite rightHandOpenSprite;
    #endregion

    #region Unity Methods
    private void Start()
    {
        InitializeExperiment();
        experimentPath = Path.Combine(Directory.GetParent(Application.dataPath).ToString(), "Experiment", $"Sub_{subjectCode}");

        if (continueExperiment)
            LoadPreviousExperiment();
        else
            SetupNewExperiment();
    }

    private void Update()
    {
        switch (currentStage)
        {
            case ExperimentStage.Distract:
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
        {
            ResetCoordinateFrame();
        }
    }
    #endregion

    #region Initialization
    private void InitializeExperiment()
    {
        localCoordinateFrame = new GameObject("LocalCoordinateFrame");
        interactionHandler = new InteractionHandler(interactionCubeLeft, interactionCubeRight);
    }

    private void LoadPreviousExperiment()
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
        TransitionToRecallSetup();
    }

    private void SetupNewExperiment()
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
    private void TransitionToSetup()
    {
        currentStage = ExperimentStage.Setup;
        SetInformationUI(center: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "Put your <b>right</b> hand into the cube and wait";
        infoPicture.sprite = rightHandOpenSprite;

        interactionHandler.PromptUser = () =>
        {
            PositionObject(informationBoard, mainCameraTransform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
            PositionObject(interactionCubeLeft, mainCameraTransform, InteractionCubePositionOffset, InteractionCubePositionOffset, Quaternion.identity, InteractionCubeDistance);
        };
        interactionHandler.OnCube1Selected = () =>
        {
            RecordCurrentTransform();
            TransitionToTutorial();
        };
        interactionHandler.OnCube2Selected = null;

        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }

    private void TransitionToTutorial()
    {
        currentStage = ExperimentStage.Tutorial;
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        EnableTutorialElements(true);
        infoTextCenter.text = "Pinch with your <b>right</b> hand to start a gesture\nRelease the pinch to finish a gesture\nConnect two dots starting from the <color=red>RED</color> dot";
        infoPicture.sprite = rightHandPinchSprite;

        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        ConfigureGestureCallbacks(TutorialStageFailed);
        SetupTutorialStep();
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToCreate()
    {
        currentStage = ExperimentStage.Create;
        if (isFeedbackActive && feedbackTimer.ElapsedMilliseconds < TutorialFeedbackIntervalMs)
            return;

        isFeedbackActive = false;
        gestureHandler.Clear();
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Create a gesture\nDraw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;

        sampleDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_create";
        Directory.CreateDirectory(Path.Combine(experimentPath, sampleDir));
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        ConfigureGestureCallbacks(CreateStageFailed, CreateStagePassed);
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToMatch()
    {
        currentStage = ExperimentStage.Match;
        gestureHandler.Clear();
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenter.text = "Match the gesture\nDraw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_match";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

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
        currentStage = ExperimentStage.Practice;
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
        currentStage = ExperimentStage.Distract;
        distractTimer.Start();
        SetInformationUI(centerBottom: true);
    }

    private void TransitionToRecall()
    {
        currentStage = ExperimentStage.Recall;
        gestureHandler.Clear();
        SetInformationUI();

        if (currentRecallAttempt >= recallAttempts || isRecallSuccessful)
        {
            stageTimer.Stop();
            isStageActive = false;
            Log($"Recall Time: {stageTimer.ElapsedMilliseconds}ms");
            Log($"Recall Fails: {(isRecallSuccessful ? currentRecallAttempt - 1 : recallAttempts)}");
            TransitionToFinish();
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

    private void TransitionToFinish()
    {
        currentStage = ExperimentStage.Finish;
        gestureHandler.Clear();
        SetInformationUI(centerBottom: true);
        infoTextCenterBottom.text = "The experiment is over. Thank you for participation!";
        totalTimer.Stop();
        Log($"Total Time: {totalTimer.ElapsedMilliseconds / 1000}s");
    }

    private void TransitionToRecallSetup()
    {
        currentStage = ExperimentStage.RecallSetup;
        gestureHandler.Clear();
        SetInformationUI(center: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "Welcome back\nPut your <b>right</b> hand into the cube and wait to continue";
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
        else if (currentStage == ExperimentStage.RecallSetup)
            TransitionToRecallSetup();
    }
    #endregion

    #region Stage Helpers
    private void SetupTutorialStep()
    {
        (string dir, Vector3 start, Vector3 end, Action onPass) setup = currentTutorialStage switch
        {
            TutorialStage.AxisX => ("AxisX", new Vector3(-0.4f, 0, 0), new Vector3(0.4f, 0, 0), () => { currentTutorialStage = TutorialStage.AxisY; PromptToContinue(TransitionToTutorial); }),
            TutorialStage.AxisY => ("AxisY", new Vector3(0, 0.4f, 0), new Vector3(0, -0.4f, 0), () => { currentTutorialStage = TutorialStage.AxisZ; PromptToContinue(TransitionToTutorial); }),
            TutorialStage.AxisZ => ("AxisZ", new Vector3(0, 0, -0.4f), new Vector3(0, 0, 0.4f), () => { currentTutorialStage = TutorialStage.Diagonal; PromptToContinue(TransitionToTutorial); }),
            TutorialStage.Diagonal => ("Diagonal", new Vector3(-0.4f, -0.4f, -0.4f), new Vector3(0.4f, 0.4f, 0.4f), () =>
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
            }
            ),
            _ => throw new InvalidOperationException("Unknown tutorial stage")
        };

        sampleDir = setup.dir;
        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_tutorial_{currentTutorialStage.ToString().ToLower()}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));
        PositionTutorialLine(setup.start, setup.end);
        gestureHandler.OnGesturePassed = () =>
        {
            setup.onPass();
            infoTextCenter.text = $"<color=green>The gesture matches the example!</color>\n\n{infoTextCenter.text}";
        };
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

    private void SetupPracticeUI()
    {
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.enabled = infoTextCenterBottom.enabled = infoPicture.enabled = true;
        infoTextCenterTop.text = $"Practice {currentPracticeAttempt}/{practiceRounds}";
        infoTextCenterBottom.text = "Draw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
        infoPicture.sprite = rightHandPinchSprite;
    }

    private void SetupRecallUI()
    {
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.enabled = infoTextCenterBottom.enabled = infoPicture.enabled = true;
        infoTextCenterTop.text = $"Recall Attempt {currentRecallAttempt}/{recallAttempts}";
        infoTextCenterBottom.text = "Draw your gesture inside the <color=#00FFFFFF>highlighted</color> area";
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
            if (currentStage == ExperimentStage.Create)
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

    private void PositionObject(GameObject obj, Transform frame, Vector3 offset, Vector3 lookOffset, Quaternion rotation, float distance)
    {
        Vector3 forward = new Vector3(frame.forward.x, 0, frame.forward.z).normalized;
        obj.transform.position = frame.position + offset + rotation * forward * (distance + obj.transform.localScale.z / 2);
        obj.transform.LookAt(frame.position + lookOffset);
        obj.transform.forward *= -1;
    }

    private void PositionTutorialLine(Vector3 start, Vector3 end)
    {
        startPoint.transform.localPosition = start;
        endPoint.transform.localPosition = end;
        tutorialLine.SetPosition(0, start);
        tutorialLine.SetPosition(1, end);
    }

    private void RecordCurrentTransform()
    {
        localCoordinateFrame.transform.SetPositionAndRotation(mainCameraTransform.position, mainCameraTransform.rotation);
    }

    private void ResetCoordinateFrame()
    {
        RecordCurrentTransform();
        PositionObject(informationBoard, localCoordinateFrame.transform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        PositionObject(interactionCubeLeft, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeLeftRotation, InteractionCubeDistance);
        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);
    }

    private void InitializeHandSubsystem(Action<XRHandSubsystem, XRHandSubsystem.UpdateSuccessFlags, XRHandSubsystem.UpdateType> onUpdate)
    {
        var handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(handSubsystems);
        handSubsystem = handSubsystems.FirstOrDefault(s => s.running);
        if (handSubsystem != null)
            handSubsystem.updatedHands += onUpdate;
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

    private void SetInformationUI(bool center = false, bool centerTop = false, bool centerBottom = false, bool left = false, bool right = false, bool picture = false)
    {
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = false;
        interactionCubeRight.GetComponent<MeshRenderer>().enabled = false;
        interactionRegion.GetComponent<LineRenderer>().enabled = false;
        EnableTutorialElements(false);

        infoTextCenter.enabled = center;
        infoTextCenterTop.enabled = centerTop;
        infoTextCenterBottom.enabled = centerBottom;
        infoTextLeft.enabled = left;
        infoTextRight.enabled = right;
        infoPicture.enabled = picture;
    }

    private void EnableTutorialElements(bool enable)
    {
        tutorialLine.enabled = enable;
        startPoint.GetComponent<MeshRenderer>().enabled = enable;
        endPoint.GetComponent<MeshRenderer>().enabled = enable;
    }

    private void Log(string message)
    {
        using (var sw = new StreamWriter(logPath, true))
        {
            string prefix = $"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}] [{DateTime.Now}] {{{currentStage}}}";
            if (currentStage == ExperimentStage.Tutorial)
                prefix += $" [{currentTutorialStage}]";
            sw.WriteLine($"{prefix} {message}");
        }
    }
    #endregion
}

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

        Task.Run(() => SaveTimestamps(dir, timestampsCopy));
        Task.Run(() => SaveHeadsetData(dir, headsetPositionsCopy, headsetRotationsCopy));
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

    private void SaveHeadsetData(string dir, List<Vector3> headsetPositionsCopy, List<Quaternion> headsetRotationsCopy)
    {
        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "headset_positions.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(headsetPositionsCopy.Count);
            foreach (var p in headsetPositionsCopy)
                sw.WriteLine($"{p.x:0.#######};{p.y:0.#######};{p.z:0.#######}");
        }

        using (var sw = new StreamWriter(Path.Combine(experimentPath, dir, "headset_rotations.csv")))
        {
            sw.WriteLine(dir);
            sw.WriteLine(headsetRotationsCopy.Count);
            foreach (var q in headsetRotationsCopy)
                sw.WriteLine($"{q.x:0.#######};{q.y:0.#######};{q.z:0.#######};{q.w:0.#######}");
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