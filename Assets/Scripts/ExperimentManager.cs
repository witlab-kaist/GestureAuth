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

internal abstract class ExperimentManager : MonoBehaviour
{
    #region Constants
    protected const double GestureScoreThreshold = 0.25f;
    protected const float BoardDistance = 4f;
    protected const float InteractionCubeDistance = 0.4f;
    protected const float InteractionRegionDistance = 0.2f;

    protected static readonly Vector3 BoardPositionOffset = new Vector3(0, 1f, 0);
    protected static readonly Vector3 InteractionCubePositionOffset = new Vector3(0, -0.195f, 0);
    protected static readonly Vector3 InteractionRegionPositionOffset = new Vector3(0, -0.195f, 0);
    protected static readonly Quaternion InteractionCubeLeftRotation = Quaternion.Euler(0, -40, 0);
    protected static readonly Quaternion InteractionCubeRightRotation = Quaternion.Euler(0, 40, 0);
    #endregion

    #region Enums
    protected enum ExperimentStage
    {
        Uninitialized,
        Setup,
        Collect,
        Finish
    }
    #endregion

    #region Fields
    protected ExperimentStage currentStage;
    protected string experimentPath, logPath;
    protected XRHandSubsystem handSubsystem;
    protected GestureHandler gestureHandler;
    protected InteractionHandler interactionHandler;
    protected GameObject localCoordinateFrame;
    protected readonly Stopwatch totalTimer = new Stopwatch();
    #endregion

    #region Serialized Fields
    [SerializeField] protected string subjectCode;
    [SerializeField] protected bool continueExperiment;
    [SerializeField] protected LineRenderer gestureTrail;
    [SerializeField] protected LineRenderer xLine;
    [SerializeField] protected LineRenderer yLine;
    [SerializeField] protected LineRenderer zLine;
    [SerializeField] protected GameObject leftMark;
    [SerializeField] protected GameObject rightMark;
    [SerializeField] protected GameObject topMark;
    [SerializeField] protected GameObject baseMark;
    [SerializeField] protected GameObject frontMark;
    [SerializeField] protected GameObject feedbackSphere;
    [SerializeField] protected Transform mainCameraTransform;
    [SerializeField] protected GameObject interactionCubeLeft;
    [SerializeField] protected GameObject interactionCubeRight;
    [SerializeField] protected GameObject interactionRegion;
    [SerializeField] protected GameObject informationBoard;
    [SerializeField] protected LineRenderer tutorialLine;
    [SerializeField] protected GameObject startPoint;
    [SerializeField] protected GameObject endPoint;
    [SerializeField] protected TextMeshProUGUI infoTextCenter;
    [SerializeField] protected TextMeshProUGUI infoTextCenterTop;
    [SerializeField] protected TextMeshProUGUI infoTextCenterBottom;
    [SerializeField] protected TextMeshProUGUI infoTextLeft;
    [SerializeField] protected TextMeshProUGUI infoTextRight;
    [SerializeField] protected Image infoPicture;
    [SerializeField] protected Sprite rightHandPinchSprite;
    [SerializeField] protected Sprite rightHandOpenSprite;
    #endregion

    #region Unity Methods
    protected virtual void Start()
    {
        InitializeExperiment();
        experimentPath = Path.Combine(Directory.GetParent(Application.dataPath).ToString(), GetExperimentType(), $"Sub_{subjectCode}");

        if (continueExperiment)
            LoadPreviousExperiment();
        else
            SetupNewExperiment();
    }

    protected virtual void Update()
    {
        if (currentStage == ExperimentStage.Setup && handSubsystem == null)
            TransitionToSetup();
    }
    #endregion

    #region Initialization
    protected virtual void InitializeExperiment()
    {
        localCoordinateFrame = new GameObject("LocalCoordinateFrame");
        interactionHandler = new InteractionHandler(interactionCubeLeft, interactionCubeRight);
    }
    protected abstract string GetExperimentType();
    protected abstract void SetupNewExperiment();
    protected abstract void LoadPreviousExperiment();
    #endregion

    #region Stage Transitions
    protected virtual void TransitionToSetup()
    {
        currentStage = ExperimentStage.Setup;
        SetInformationUI(center: true, picture: true);
        interactionCubeLeft.GetComponent<MeshRenderer>().enabled = true;
        infoTextCenter.text = "Put your <b>right</b> hand into the cube and wait to start";
        infoPicture.sprite = rightHandOpenSprite;

        interactionHandler.PromptUser = () =>
        {
            PositionObject(informationBoard, mainCameraTransform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
            PositionObject(interactionCubeLeft, mainCameraTransform, InteractionCubePositionOffset, InteractionCubePositionOffset, Quaternion.identity, InteractionCubeDistance);
        };
        interactionHandler.OnCube1Selected = () =>
        {
            RecordCurrentTransform();
            TransitionToNextStage();
        };
        interactionHandler.OnCube2Selected = null;

        InitializeHandSubsystem(interactionHandler.OnUpdatedHandsPromptUser);
    }

    protected abstract void TransitionToNextStage();
    #endregion

    #region Helpers
    protected List<Vector3> LoadGesturePoints(string dir)
    {
        string filePath = Path.Combine(experimentPath, dir, "ex.txt");
        if (!File.Exists(filePath))
        {
            filePath = Path.Combine(Directory.GetParent(Application.dataPath).ToString(), "Gestures", dir, "ex.txt");
            if (!File.Exists(filePath)) throw new FileNotFoundException($"ex.txt not found in {dir}");
        }
        var points = new List<Vector3>();
        using (var sr = new StreamReader(filePath))
        {
            sr.ReadLine();
            int count = int.Parse(sr.ReadLine());
            for (int i = 0; i < count; i++)
            {
                sr.ReadLine();
                string[] coords = sr.ReadLine().Split(',');
                float x = float.Parse(coords[0]);
                float y = float.Parse(coords[1]);
                float z = float.Parse(coords[2]);
                points.Add(new Vector3(x, y, z));
            }
        }
        return points;
    }

    protected void PositionObject(GameObject obj, Transform frame, Vector3 offset, Vector3 lookOffset, Quaternion rotation, float distance)
    {
        Vector3 forward = new Vector3(frame.forward.x, 0, frame.forward.z).normalized;
        obj.transform.position = frame.position + offset + rotation * forward * (distance + obj.transform.localScale.z / 2);
        obj.transform.LookAt(frame.position + lookOffset);
        obj.transform.forward *= -1;
    }

    protected void RecordCurrentTransform()
    {
        localCoordinateFrame.transform.SetPositionAndRotation(mainCameraTransform.position, mainCameraTransform.rotation);
    }

    protected void ResetCoordinateFrame()
    {
        RecordCurrentTransform();
        PositionObject(informationBoard, localCoordinateFrame.transform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);
        PositionObject(interactionCubeLeft, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeLeftRotation, InteractionCubeDistance);
        PositionObject(interactionCubeRight, localCoordinateFrame.transform, InteractionCubePositionOffset, InteractionCubePositionOffset, InteractionCubeRightRotation, InteractionCubeDistance);
    }

    protected void InitializeHandSubsystem(Action<XRHandSubsystem, XRHandSubsystem.UpdateSuccessFlags, XRHandSubsystem.UpdateType> onUpdate)
    {
        var handSubsystems = new List<XRHandSubsystem>();
        SubsystemManager.GetSubsystems(handSubsystems);
        handSubsystem = handSubsystems.FirstOrDefault(s => s.running);
        if (handSubsystem != null)
            handSubsystem.updatedHands += onUpdate;
    }

    protected void SetInformationUI(bool center = false, bool centerTop = false, bool centerBottom = false, bool left = false, bool right = false, bool picture = false)
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

    protected void EnableTutorialElements(bool enable)
    {
        tutorialLine.enabled = enable;
        startPoint.GetComponent<MeshRenderer>().enabled = enable;
        endPoint.GetComponent<MeshRenderer>().enabled = enable;
    }

    protected void Log(string message)
    {
        using (var sw = new StreamWriter(logPath, true))
        {
            string prefix = $"[{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}] [{DateTime.Now}] {{{currentStage}}}";
            sw.WriteLine($"{prefix} {message}");
        }
    }
    #endregion
}