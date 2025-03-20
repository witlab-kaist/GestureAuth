using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

internal class CollectionManager : ExperimentManager
{
    #region Fields
    private List<List<string>> blocks;
    private int currentBlockIndex;
    private int currentGestureIndex;
    private int currentAttempt;
    private TrailHandler tutorialTrail;
    #endregion

    #region Serialized Fields
    [SerializeField] private int maxAttempts;
    [SerializeField] private int nBlocks;
    [SerializeField] private int mGesturesPerType;
    #endregion

    #region Unity Methods
    protected override void Start()
    {
        base.Start();
        tutorialTrail = new TrailHandler(tutorialLine);
    }

    protected override void Update()
    {
        base.Update();
        if (Input.GetKeyDown(KeyCode.Space))
            ResetCoordinateFrame();
    }
    #endregion

    #region Initialization
    protected override string GetExperimentType() => "Collection";

    protected override void SetupNewExperiment()
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        logPath = Path.Combine(experimentPath, $"{timestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", timestamp);

        if (!Directory.Exists(experimentPath))
            Directory.CreateDirectory(experimentPath);

        string gesturesPath = Path.Combine(Directory.GetParent(Application.dataPath).ToString(), "Gestures");
        var gestureSubdirs = Directory.GetDirectories(gesturesPath).Select(Path.GetFileName).ToList();
        gestureSubdirs.Sort();

        blocks = new List<List<string>>();
        for (int i = 0; i < nBlocks; i++)
        {
            List<string> block = new List<string>();
            foreach (var gesture in gestureSubdirs)
                for (int j = 0; j < mGesturesPerType; j++)
                    block.Add(gesture);
            block = block.OrderBy(x => UnityEngine.Random.value).ToList();
            blocks.Add(block);
        }

        foreach (var subdir in gestureSubdirs)
        {
            string sampleDir = Path.Combine(experimentPath, subdir);
            Directory.CreateDirectory(sampleDir);
            File.Copy(Path.Combine(gesturesPath, subdir, "ex.txt"), Path.Combine(sampleDir, "ex.txt"));
        }

        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);
        totalTimer.Start();
        TransitionToSetup();
    }

    protected override void LoadPreviousExperiment()
    {
        throw new NotImplementedException("LoadPreviousExperiment not implemented.");
    }
    #endregion

    #region Stage Transitions
    protected override void TransitionToNextStage()
    {
        if (currentStage == ExperimentStage.Setup)
            TransitionToCollect();
    }

    private void TransitionToCollect()
    {
        currentStage = ExperimentStage.Collect;
        SetInformationUI(center: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        EnableTutorialElements(true);
        infoTextCenter.text = "Pinch with your <b>right</b> hand to start a gesture\nRelease the pinch to finish a gesture\nConnect two dots starting from the <color=red>RED</color> dot";
        infoPicture.sprite = rightHandPinchSprite;

        PositionObject(informationBoard, localCoordinateFrame.transform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        SetupCurrentGesture();

        gestureHandler.OnGestureStarted = () => Log($"Started drawing gesture {blocks[currentBlockIndex][currentGestureIndex]}, attempt {currentAttempt + 1}");
        gestureHandler.OnGestureFinished = ProcessGesture;
        gestureHandler.OnGesturePassed = null;
        gestureHandler.OnGestureFailed = null;
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }

    private void TransitionToFinish()
    {
        currentStage = ExperimentStage.Finish;
        gestureHandler.Clear();
        tutorialTrail.Clear();
        SetInformationUI(center: true);
        infoTextCenter.text = "The experiment is over. Thank you for participation!";
        totalTimer.Stop();
        Log($"Total Time: {totalTimer.ElapsedMilliseconds / 1000}s");
    }
    #endregion

    #region Stage Helpers
    private void SetupCurrentGesture()
    {
        var points = LoadGesturePoints(blocks[currentBlockIndex][currentGestureIndex]);
        startPoint.transform.localPosition = points[0];
        endPoint.transform.localPosition = points[points.Count - 1];
        for (int i = 0; i < points.Count; i++)
        {
            tutorialTrail.DrawPoint(points[i]);
        }
    }

    private void ProcessGesture()
    {
        currentAttempt++;
        string queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{blocks[currentBlockIndex][currentGestureIndex]}_block{currentBlockIndex}_attempt{currentAttempt}";
        string fullQueryDir = Path.Combine(experimentPath, queryDir);
        Directory.CreateDirectory(fullQueryDir);
        gestureHandler.Save(queryDir);
        double score = gestureHandler.JackknifeScore(blocks[currentBlockIndex][currentGestureIndex], queryDir);
        Log($"Gesture {blocks[currentBlockIndex][currentGestureIndex]}, attempt {currentAttempt}: score {score:0.#######}");
        bool tooManyFailedAttempts = currentAttempt >= maxAttempts;
        if (score >= GestureScoreThreshold || tooManyFailedAttempts)
        {
            if (tooManyFailedAttempts)
            {
                Log($"Gesture {blocks[currentBlockIndex][currentGestureIndex]} failed after {maxAttempts} attempts");
                infoTextCenter.text = "<color=red>Maximum attempts reached. Moving to next gesture.</color>";
            }
            else
            {
                Log($"Gesture {blocks[currentBlockIndex][currentGestureIndex]} matched successfully on attempt {currentAttempt}");
                infoTextCenter.text = "<color=green>The gesture matches!</color>";
            }
            currentGestureIndex++;
            currentAttempt = 0;
            gestureHandler.Clear();
            tutorialTrail.Clear();
            if (currentGestureIndex < blocks[currentBlockIndex].Count)
            {
                SetupCurrentGesture();
                InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
            }
            else
            {
                currentBlockIndex++;
                if (currentBlockIndex < blocks.Count)
                {
                    currentGestureIndex = 0;
                    SetupCurrentGesture();
                    InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
                }
                else
                {
                    TransitionToFinish();
                }
            }
        }
        else
        {
            Log($"Gesture {blocks[currentBlockIndex][currentGestureIndex]} did not match");
            infoTextCenter.text = "<color=red>The gesture doesn't match! Try again.</color>";
            gestureHandler.Clear();
            InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
        }
    }
    #endregion
}