using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Perception.GroundTruth;
using UnityEngine.Rendering;

#if HDRP_PRESENT
    using UnityEngine.Rendering.HighDefinition;
#elif URP_PRESENT
    using UnityEngine.Rendering.Universal;
#endif

using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GroundTruthTests
{
    public class ImageReaderBehaviour : MonoBehaviour
    {
        public RenderTexture source;
        public Camera cameraSource;

        public event Action<int, NativeArray<Color32>> SegmentationImageReceived;

        void Awake()
        {
            RenderPipelineManager.endCameraRendering += (context, cam) =>
                RenderTextureReader.Capture<Color32>(context, source, ImageReadCallback);
        }

        void ImageReadCallback(int frameCount, NativeArray<Color32> data, RenderTexture renderTexture)
        {
            SegmentationImageReceived?.Invoke(frameCount, data);
        }
    }

    public enum RendererType
    {
        MeshRenderer,
        SkinnedMeshRenderer,
        Terrain
    }

    //Graphics issues with OpenGL Linux Editor. https://jira.unity3d.com/browse/AISV-422
    [UnityPlatform(exclude = new[] {RuntimePlatform.LinuxEditor, RuntimePlatform.LinuxPlayer})]
    public class SegmentationPassTests : GroundTruthTestBase
    {
        static readonly Color32 k_SemanticPixelValue = new Color32(10, 20, 30, Byte.MaxValue);
        static readonly Color32 k_InstanceSegmentationPixelValue = new Color32(255,0,0, 255);
        static readonly Color32 k_SkyValue = new Color32(10, 20, 30, 40);

        public enum SegmentationKind
        {
            Instance,
            Semantic
        }
        // A UnityTest behaves like a coroutine in Play Mode. In Edit Mode you can use
        // `yield return null;` to skip a frame.
        [UnityTest]
        public IEnumerator SegmentationPassTestsWithEnumeratorPasses(
            [Values(RendererType.MeshRenderer, RendererType.SkinnedMeshRenderer, RendererType.Terrain)] RendererType rendererType,
            [Values(SegmentationKind.Instance, SegmentationKind.Semantic)] SegmentationKind segmentationKind)
        {
            int timesSegmentationImageReceived = 0;
            int? frameStart = null;
            GameObject cameraObject = null;

            object expectedPixelValue;
            void OnSegmentationImageReceived<T>(int frameCount, NativeArray<T> data, RenderTexture tex) where T : struct
            {
                if (frameStart == null || frameStart > frameCount) return;

                timesSegmentationImageReceived++;
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data.ToArray());
            }

            switch (segmentationKind)
            {
                case SegmentationKind.Instance:
                    expectedPixelValue = k_InstanceSegmentationPixelValue;
                    cameraObject = SetupCameraInstanceSegmentation(OnSegmentationImageReceived);
                    break;
                case SegmentationKind.Semantic:
                    expectedPixelValue = k_SemanticPixelValue;
                    cameraObject = SetupCameraSemanticSegmentation(a => OnSegmentationImageReceived(a.frameCount, a.data, a.sourceTexture), false);
                    break;
            }

            //Put a plane in front of the camera
            GameObject planeObject;
            if (rendererType == RendererType.Terrain)
            {
                var terrainData = new TerrainData();
                AddTestObjectForCleanup(terrainData);
                //look down because terrains cannot be rotated
                cameraObject.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                planeObject = Terrain.CreateTerrainGameObject(terrainData);
                planeObject.transform.SetPositionAndRotation(new Vector3(-10, -10, -10), Quaternion.identity);
            }
            else
            {
                planeObject = GameObject.CreatePrimitive(PrimitiveType.Plane);
                if (rendererType == RendererType.SkinnedMeshRenderer)
                {
                    var oldObject = planeObject;
                    planeObject = new GameObject();

                    var meshFilter = oldObject.GetComponent<MeshFilter>();
                    var meshRenderer = oldObject.GetComponent<MeshRenderer>();
                    var skinnedMeshRenderer = planeObject.AddComponent<SkinnedMeshRenderer>();
                    skinnedMeshRenderer.sharedMesh = meshFilter.sharedMesh;
                    skinnedMeshRenderer.material = meshRenderer.material;

                    Object.DestroyImmediate(oldObject);
                }
                planeObject.transform.SetPositionAndRotation(new Vector3(0, 0, 10), Quaternion.Euler(90, 0, 0));
                planeObject.transform.localScale = new Vector3(10, -1, 10);
            }
            var labeling = planeObject.AddComponent<Labeling>();
            labeling.labels.Add("label");

            frameStart = Time.frameCount;

            AddTestObjectForCleanup(planeObject);

            yield return null;
            yield return null;
            yield return null;
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            DestroyTestObject(planeObject);

            Assert.AreEqual(4, timesSegmentationImageReceived);
        }

        // Lens Distortion is only applicable in URP or HDRP pipelines
        // As such, this test will always fail if URP or HDRP are not present (and also not really compile either)
#if HDRP_PRESENT || URP_PRESENT
        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithLensDistortion()
        {
            GameObject cameraObject = null;
            PerceptionCamera perceptionCamera;
            var fLensDistortionEnabled = false;
            var fDone = false;
            var frames = 0;

            var boundingBoxWithoutLensDistortion = new Rect();
            var boundingBoxWithLensDistortion = new Rect();

            void OnSegmentationImageReceived(int frameCount, NativeArray<Color32> data, RenderTexture tex)
            {
                frames++;

                if (frames < 10)
                    return;

                // Calculate the bounding box
                if (fLensDistortionEnabled == false)
                {
                    fLensDistortionEnabled = true;

                    var renderedObjectInfoGenerator = new RenderedObjectInfoGenerator();
                    renderedObjectInfoGenerator.Compute(data, tex.width, BoundingBoxOrigin.TopLeft, out var boundingBoxes, Allocator.Temp);

                    boundingBoxWithoutLensDistortion = boundingBoxes[0].boundingBox;

                    // Add lens distortion
                    perceptionCamera.OverrideLensDistortionIntensity(0.715f);

                    frames = 0;
                }
                else
                {
                    var renderedObjectInfoGenerator = new RenderedObjectInfoGenerator();

                    renderedObjectInfoGenerator.Compute(data, tex.width, BoundingBoxOrigin.TopLeft, out var boundingBoxes, Allocator.Temp);

                    boundingBoxWithLensDistortion = boundingBoxes[0].boundingBox;

                    Assert.AreNotEqual(boundingBoxWithoutLensDistortion, boundingBoxWithLensDistortion);
                    Assert.Greater(boundingBoxWithLensDistortion.width, boundingBoxWithoutLensDistortion.width);

                    fDone = true;
                }
            }

            cameraObject = SetupCamera(out perceptionCamera, false);
            perceptionCamera.InstanceSegmentationImageReadback += OnSegmentationImageReceived;
            cameraObject.SetActive(true);

            // Put a plane in front of the camera
            var planeObject = GameObject.CreatePrimitive(PrimitiveType.Plane);

            planeObject.transform.SetPositionAndRotation(new Vector3(0, 0, 10), Quaternion.Euler(90, 0, 0));
            planeObject.transform.localScale = new Vector3(0.1f, -1, 0.1f);
            var labeling = planeObject.AddComponent<Labeling>();
            labeling.labels.Add("label");

            AddTestObjectForCleanup(planeObject);

            perceptionCamera.OverrideLensDistortionIntensity(0.5f);

            while (fDone != true)
            {
                yield return null;
            }

            // Destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            DestroyTestObject(planeObject);
        }
#endif // ! HDRP_PRESENT || URP_PRESENT

        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithLabeledButNotMatchingObject_ProducesBlack()
        {
            int timesSegmentationImageReceived = 0;
            var expectedPixelValue = new Color32(0, 0, 0, 255);
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                timesSegmentationImageReceived++;
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data.ToArray());
            }

            var cameraObject = SetupCameraSemanticSegmentation(a => OnSegmentationImageReceived(a.data), false, k_SkyValue);

            AddTestObjectForCleanup(TestHelper.CreateLabeledPlane(label: "non-matching"));
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }

        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithMatchingButDisabledLabel_ProducesBlack()
        {
            int timesSegmentationImageReceived = 0;
            var expectedPixelValue = new Color32(0, 0, 0, 255);
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                timesSegmentationImageReceived++;
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data.ToArray());
            }

            var cameraObject = SetupCameraSemanticSegmentation(a => OnSegmentationImageReceived(a.data), false, k_SkyValue);

            var gameObject = TestHelper.CreateLabeledPlane();
            gameObject.GetComponent<Labeling>().enabled = false;
            AddTestObjectForCleanup(gameObject);
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }

        [UnityTest]
        public IEnumerator InstanceSegmentationPass_WithMatchingButDisabledLabel_ProducesBlack()
        {
            int timesSegmentationImageReceived = 0;
            var expectedPixelValue = new Color32(0, 0, 0, 255);
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data);
                timesSegmentationImageReceived++;
            }

            var cameraObject = SetupCameraInstanceSegmentation((frame, data, renderTexture) => OnSegmentationImageReceived(data));

            var gameObject = TestHelper.CreateLabeledPlane();
            gameObject.GetComponent<Labeling>().enabled = false;
            AddTestObjectForCleanup(gameObject);
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }

        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithEmptyFrame_ProducesSky([Values(false, true)] bool showVisualizations)
        {
            var timesSegmentationImageReceived = 0;
            var expectedPixelValue = k_SkyValue;
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                timesSegmentationImageReceived++;
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data.ToArray());
            }

            var cameraObject = SetupCameraSemanticSegmentation(
                args => OnSegmentationImageReceived(args.data), showVisualizations, expectedPixelValue);

            yield return null;

            // Destroy the camera to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }

        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithNoObjects_ProducesSky()
        {
            int timesSegmentationImageReceived = 0;
            var expectedPixelValue = k_SkyValue;
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                timesSegmentationImageReceived++;
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data.ToArray());
            }

            var cameraObject = SetupCameraSemanticSegmentation(
                a => OnSegmentationImageReceived(a.data), false, expectedPixelValue);

            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }

        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithTextureOverride_RendersToOverride([Values(true, false)] bool showVisualizations)
        {
            var expectedPixelValue = new Color32(0, 0, 255, 255);
            var targetTextureOverride = new RenderTexture(2, 2, 1, RenderTextureFormat.R8);

            var cameraObject = SetupCamera(out var perceptionCamera, showVisualizations);
            var labelConfig = ScriptableObject.CreateInstance<SemanticSegmentationLabelConfig>();
            labelConfig.Init(new List<SemanticSegmentationLabelEntry>()
            {
                new SemanticSegmentationLabelEntry()
                {
                    label = "label",
                    color = expectedPixelValue
                }
            });
            var semanticSegmentationLabeler = new SemanticSegmentationLabeler(labelConfig, targetTextureOverride);
            perceptionCamera.AddLabeler(semanticSegmentationLabeler);
            cameraObject.SetActive(true);
            AddTestObjectForCleanup(cameraObject);
            AddTestObjectForCleanup(TestHelper.CreateLabeledPlane());

            yield return null;
            TestHelper.ReadRenderTextureRawData<Color32>(targetTextureOverride, data =>
            {
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, targetTextureOverride.width * targetTextureOverride.height), data);
            });
        }


        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithMultiMaterial_ProducesCorrectValues([Values(true, false)] bool showVisualizations)
        {
            int timesSegmentationImageReceived = 0;
            var expectedPixelValue = k_SemanticPixelValue;
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                timesSegmentationImageReceived++;
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data);
            }

            var cameraObject = SetupCameraSemanticSegmentation(a => OnSegmentationImageReceived(a.data), false);

            var plane = TestHelper.CreateLabeledPlane();
            var meshRenderer = plane.GetComponent<MeshRenderer>();
            var baseMaterial = meshRenderer.material;
            meshRenderer.materials = new[] { baseMaterial, baseMaterial };
            MaterialPropertyBlock mpb = new MaterialPropertyBlock();
            mpb.SetFloat("float", 1f);
            for (int i = 0; i < 2; i++)
            {
                meshRenderer.SetPropertyBlock(mpb, i);
            }
            AddTestObjectForCleanup(plane);
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }


        [UnityTest]
        public IEnumerator SemanticSegmentationPass_WithChangingLabeling_ProducesCorrectValues([Values(true, false)] bool showVisualizations)
        {
            int timesSegmentationImageReceived = 0;
            var expectedPixelValue = k_SemanticPixelValue;
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                if (timesSegmentationImageReceived == 1)
                {
                    CollectionAssert.AreEqual(Enumerable.Repeat(expectedPixelValue, data.Length), data);
                }
                timesSegmentationImageReceived++;
            }

            var cameraObject = SetupCameraSemanticSegmentation(a => OnSegmentationImageReceived(a.data), false);

            var plane = TestHelper.CreateLabeledPlane(label: "non-matching");
            AddTestObjectForCleanup(plane);
            yield return null;
            var labeling = plane.GetComponent<Labeling>();
            labeling.labels = new List<string> { "label" };
            labeling.RefreshLabeling();
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            Assert.AreEqual(2, timesSegmentationImageReceived);
        }


        [UnityTest]
        public IEnumerator InstanceSegmentationPass_WithSeparateDisabledPerceptionCamera_ProducesCorrectValues()
        {
            int timesSegmentationImageReceived = 0;
            void OnSegmentationImageReceived(NativeArray<Color32> data)
            {
                CollectionAssert.AreEqual(Enumerable.Repeat(k_InstanceSegmentationPixelValue, data.Length), data);
                timesSegmentationImageReceived++;
            }

            var cameraObject = SetupCameraInstanceSegmentation((frame, data, renderTexture) => OnSegmentationImageReceived(data));
            var cameraObject2 = SetupCameraInstanceSegmentation(null);
            cameraObject2.SetActive(false);

            var plane = TestHelper.CreateLabeledPlane();
            AddTestObjectForCleanup(plane);
            yield return null;
            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            DestroyTestObject(cameraObject2);
            Assert.AreEqual(1, timesSegmentationImageReceived);
        }


        [UnityTest]
        public IEnumerator SegmentationPass_WithMultiplePerceptionCameras_ProducesCorrectValues(
            [Values(SegmentationKind.Instance, SegmentationKind.Semantic)] SegmentationKind segmentationKind)
        {
            int timesSegmentationImageReceived = 0;

            var color1 = segmentationKind == SegmentationKind.Instance ?
                k_InstanceSegmentationPixelValue :
                k_SemanticPixelValue;
            var color2 = segmentationKind == SegmentationKind.Instance ?
                new Color32(0, 74, Byte.MaxValue, Byte.MaxValue) :
                new Color32(0, 0, 0, Byte.MaxValue);
            void OnCam1SegmentationImageReceived(NativeArray<Color32> data)
            {
                CollectionAssert.AreEqual(Enumerable.Repeat(color1, data.Length), data);
                timesSegmentationImageReceived++;
            }
            void OnCam2SegmentationImageReceived(NativeArray<Color32> data)
            {
                Assert.AreEqual(color1, data[data.Length / 4]);
                Assert.AreEqual(color2, data[data.Length * 3 / 4]);
                timesSegmentationImageReceived++;
            }

            GameObject cameraObject;
            GameObject cameraObject2;
            if (segmentationKind == SegmentationKind.Instance)
            {
                cameraObject = SetupCameraInstanceSegmentation((frame, data, renderTexture) => OnCam1SegmentationImageReceived(data));
                cameraObject2 = SetupCameraInstanceSegmentation((frame, data, renderTexture) => OnCam2SegmentationImageReceived(data));
            }
            else
            {
                cameraObject = SetupCameraSemanticSegmentation((args) => OnCam1SegmentationImageReceived(args.data), false);
                cameraObject2 = SetupCameraSemanticSegmentation((args) => OnCam2SegmentationImageReceived(args.data), false);
            }
            //position camera to point straight at the top edge of plane1, such that plane1 takes up the bottom half of
            //the image and plane2 takes up the top half
            cameraObject2.transform.localPosition = Vector3.up * 10f;

            var plane1 = TestHelper.CreateLabeledPlane(2f);
            var plane2 = TestHelper.CreateLabeledPlane(2f, "label2");
            plane2.transform.localPosition = plane2.transform.localPosition + Vector3.up * 20f;
            AddTestObjectForCleanup(plane1);
            AddTestObjectForCleanup(plane2);
            yield return null;

            //destroy the object to force all pending segmented image readbacks to finish and events to be fired.
            DestroyTestObject(cameraObject);
            DestroyTestObject(cameraObject2);
            Assert.AreEqual(2, timesSegmentationImageReceived);
        }

        [UnityTest]
        public IEnumerator SegmentationPassProducesCorrectValuesEachFrame(
            [Values(SegmentationKind.Instance, SegmentationKind.Semantic)] SegmentationKind segmentationKind)
        {
            var timesSegmentationImageReceived = 0;
            var expectedPixelValue = segmentationKind == SegmentationKind.Instance
                ? k_InstanceSegmentationPixelValue : k_SemanticPixelValue;
            var expectedBackgroundPixelColorAtFrame = new Dictionary<int, Color32>
            {
                {Time.frameCount    , expectedPixelValue},
                {Time.frameCount + 1, expectedPixelValue},
                {Time.frameCount + 2, expectedPixelValue}
            };

            void OnSegmentationImageReceived(int frameCount, NativeArray<Color32> data, RenderTexture tex)
            {
                if (!expectedBackgroundPixelColorAtFrame.ContainsKey(frameCount))
                    return;

                timesSegmentationImageReceived++;
                var expectedColor = expectedBackgroundPixelColorAtFrame[frameCount];
                CollectionAssert.AreEqual(Enumerable.Repeat(expectedColor, data.Length), data.ToArray());
            }

            var cameraObject = segmentationKind == SegmentationKind.Instance ?
                SetupCameraInstanceSegmentation(OnSegmentationImageReceived) :
                SetupCameraSemanticSegmentation(args =>
                    OnSegmentationImageReceived(args.frameCount, args.data, args.sourceTexture), false);

            // Put a plane in front of the camera to force the background of the
            // segmentation images to be a color other than black.
            var planeObject = TestHelper.CreateLabeledPlane();

            // Wait 3 frames
            for (var i = 0; i < 3; i++)
                yield return null;

            // Destroy the camera to force all pending segmentation image readbacks and subsequent callbacks to finish
            DestroyTestObject(cameraObject);
            Object.DestroyImmediate(planeObject);

            Assert.AreEqual(3, timesSegmentationImageReceived);
        }

        GameObject SetupCameraInstanceSegmentation(Action<int, NativeArray<Color32>, RenderTexture> onSegmentationImageReceived)
        {
            var cameraObject = SetupCamera(out var perceptionCamera, false);
            perceptionCamera.InstanceSegmentationImageReadback += onSegmentationImageReceived;
            cameraObject.SetActive(true);
            return cameraObject;
        }

        GameObject SetupCameraSemanticSegmentation(Action<SemanticSegmentationLabeler.ImageReadbackEventArgs> onSegmentationImageReceived, bool showVisualizations, Color? backgroundColor = null)
        {
            var cameraObject = SetupCamera(out var perceptionCamera, showVisualizations);
            var labelConfig = ScriptableObject.CreateInstance<SemanticSegmentationLabelConfig>();
            labelConfig.Init(new List<SemanticSegmentationLabelEntry>()
            {
                new SemanticSegmentationLabelEntry()
                {
                    label = "label",
                    color = k_SemanticPixelValue
                }
            });
            if (backgroundColor != null)
            {
                labelConfig.skyColor = backgroundColor.Value;
            }
            var semanticSegmentationLabeler = new SemanticSegmentationLabeler(labelConfig);
            semanticSegmentationLabeler.imageReadback += onSegmentationImageReceived;
            perceptionCamera.AddLabeler(semanticSegmentationLabeler);
            cameraObject.SetActive(true);
            return cameraObject;
        }

        GameObject SetupCamera(out PerceptionCamera perceptionCamera, bool showVisualizations)
        {
            var cameraObject = new GameObject();
            cameraObject.SetActive(false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 1;
            perceptionCamera = cameraObject.AddComponent<PerceptionCamera>();
            perceptionCamera.captureRgbImages = false;
            perceptionCamera.showVisualizations = showVisualizations;

            AddTestObjectForCleanup(cameraObject);
            return cameraObject;
        }
    }
}
