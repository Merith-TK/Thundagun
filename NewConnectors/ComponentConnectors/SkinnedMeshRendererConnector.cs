using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using UnityFrooxEngineRunner;
using Mesh = UnityEngine.Mesh;
using Object = UnityEngine.Object;
using Transform = UnityEngine.Transform;

namespace Thundagun.NewConnectors.ComponentConnectors;

public class SkinnedMeshRendererConnector :
    MeshRendererConnectorBase<SkinnedMeshRenderer, UnityEngine.SkinnedMeshRenderer>,
    ISkinnedMeshRendererConnector
{
    public SkinnedMeshRendererConnector _proxySource;
    public SkinBoundsUpdater _boundsUpdater;
    public Transform[] bones;
    private bool _sendingBoundsUpdate;
    public bool _forceRecalcPerRender;
    public SkinnedBounds _currentBoundsMethod = ~SkinnedBounds.Static;

    public bool LocalBoundingBoxAvailable { get; internal set; }

    public BoundingBox LocalBoundingBox { get; internal set; }

    public event Action BoundsUpdated;

    public override bool UseMeshFilter => false;

    public bool ForceRecalcActive => throw new NotImplementedException();

    public override void AssignMesh(UnityEngine.SkinnedMeshRenderer renderer, Mesh mesh) => renderer.sharedMesh = mesh;

    public void CleanupBoundsUpdater()
    {
        if (_boundsUpdater)
            Object.Destroy(_boundsUpdater);
        _boundsUpdater = null;
    }

    public override void OnCleanupRenderer()
    {
        base.OnCleanupRenderer();
        if (!(_boundsUpdater != null))
            return;
        CleanupBoundsUpdater();
    }

    public void ForceRecalculationPerRender()
    {
        _forceRecalcPerRender = true;
        if (!(MeshRenderer != null))
            return;
        MeshRenderer.forceMatrixRecalculationPerRender = true;
    }

    public void SendBoundsUpdated()
    {
        if (_sendingBoundsUpdate)
            return;
        try
        {
            _sendingBoundsUpdate = true;
            if (BoundsUpdated == null)
                return;
            BoundsUpdated();
        }
        finally
        {
            _sendingBoundsUpdate = false;
        }
    }

    public void CleanupProxy()
    {
        if (_proxySource == null)
            return;
        _proxySource.BoundsUpdated -= ProxyBoundsUpdated;
        _proxySource = null;
    }

    public void ProxyBoundsUpdated()
    {
        if (!(MeshRenderer != null) || !(_proxySource.MeshRenderer != null))
            return;
        MeshRenderer.localBounds = _proxySource.MeshRenderer.localBounds;
    }

    public override void OnAttachRenderer()
    {
        base.OnAttachRenderer();
        Owner.BlendShapeWeightsChanged = true;
    }
    public override void DestroyMethod(bool destroyingWorld)
    {
        CleanupProxy();
        BoundsUpdated = null;
        if (_boundsUpdater != null)
        {
            if (!destroyingWorld && _boundsUpdater) Object.Destroy(_boundsUpdater);
            _boundsUpdater = null;
        }
        bones = null;
        base.DestroyMethod(destroyingWorld);
    }
}

public class ApplyChangesSkinnedMeshRenderer : ApplyChangesMeshRendererConnectorBase<SkinnedMeshRenderer,
    UnityEngine.SkinnedMeshRenderer>
{
    public SkinnedBounds SkinnedBounds;
    public bool BoundsChanged;
    public List<BoneMetadata> BoneMetadata;
    public List<ApproximateBoneBounds> ApproximateBounds;
    public SkinnedMeshRendererConnector Proxy;
    public UnityEngine.Bounds Bounds;
    
    public bool BonesChanged;
    public bool BlendShapeWeightsChanged;
    public int? BoneCount;
    public int? BlendShapeCount;

    public List<SlotConnector> Bones;
    public SlotConnector RootBone;
    public List<float> BlendShapeWeights;
    
    public SkinnedMeshRendererConnector Skinned => Owner as SkinnedMeshRendererConnector;
        
    public ApplyChangesSkinnedMeshRenderer(SkinnedMeshRendererConnector owner) : base(owner)
    {
        SkinnedBounds = owner.Owner.BoundsComputeMethod.Value;
        if (SkinnedBounds == SkinnedBounds.Static && owner.Owner.Slot.ActiveUserRoot == owner.Owner.LocalUserRoot)
            SkinnedBounds = SkinnedBounds.FastDisjointRootApproximate;
        
        BoundsChanged = owner.Owner.ProxyBoundsSource.WasChanged || owner.Owner.ExplicitLocalBounds.WasChanged;
        owner.Owner.ProxyBoundsSource.WasChanged = false;
        owner.Owner.ExplicitLocalBounds.WasChanged = false;

        switch (SkinnedBounds)
        {
            case SkinnedBounds.Proxy:
                Proxy = owner.Owner.ProxyBoundsSource.Target?.SkinConnector as SkinnedMeshRendererConnector;
                break;
            case SkinnedBounds.Static:
                break;
            case SkinnedBounds.Explicit:
                Bounds = owner.Owner.ExplicitLocalBounds.Value.ToUnity();
                break;
            case SkinnedBounds.FastDisjointRootApproximate:
            case SkinnedBounds.MediumPerBoneApproximate:
            case SkinnedBounds.SlowRealtimeAccurate:
                BoneMetadata = new List<BoneMetadata>(owner.Owner.Mesh.Asset.BoneMetadata);
                ApproximateBounds = new List<ApproximateBoneBounds>(owner.Owner.Mesh.Asset.ApproximateBoneBounds);
                break;
        }

        BonesChanged = owner.Owner.BonesChanged;
        owner.Owner.BonesChanged = false;
        
        BlendShapeCount = owner.Owner.Mesh?.Asset?.Data?.BlendShapeCount;

        if (BonesChanged || MeshWasChanged)
        {
            BoneCount = owner.Owner.Mesh?.Asset?.Data?.BoneCount;
            Bones = owner.Owner.Bones.Select(i => i.Connector as SlotConnector).ToList();
            RootBone = owner.Owner.GetRootBone()?.Connector as SlotConnector;
        }

        BlendShapeWeightsChanged = owner.Owner.BlendShapeWeightsChanged;
        owner.Owner.BlendShapeWeightsChanged = false;
        
        if (BlendShapeWeightsChanged || MeshWasChanged)
        {
            BlendShapeWeights = new List<float>(owner.Owner.BlendShapeWeights);
        }
    }

    public override void OnUpdateRenderer(bool instantiated)
    {
        var skinnedBounds = SkinnedBounds;
        if (MeshWasChanged || Skinned._currentBoundsMethod != skinnedBounds || BoundsChanged)
        {
            if (skinnedBounds != SkinnedBounds.Static && skinnedBounds != SkinnedBounds.Proxy &&
                skinnedBounds != SkinnedBounds.Explicit)
            {
                if (Skinned._boundsUpdater == null)
                {
                    Skinned.LocalBoundingBoxAvailable = false;
                    Skinned._boundsUpdater = Skinned.MeshRenderer.gameObject.AddComponent<SkinBoundsUpdater>();
                    Skinned._boundsUpdater.connector = Skinned;
                }
                Skinned._boundsUpdater.boundsMethod = skinnedBounds;
                Skinned._boundsUpdater.boneMetadata = BoneMetadata;
                Skinned._boundsUpdater.approximateBounds = ApproximateBounds;
                Skinned.MeshRenderer.updateWhenOffscreen = skinnedBounds == SkinnedBounds.SlowRealtimeAccurate;
            }
            else
            {
                if (Skinned._boundsUpdater != null)
                {
                    Skinned.LocalBoundingBoxAvailable = false;
                    Skinned.MeshRenderer.updateWhenOffscreen = false;
                    Skinned.CleanupBoundsUpdater();
                }

                if (skinnedBounds == SkinnedBounds.Proxy)
                {
                    Skinned.CleanupProxy();
                    Skinned._proxySource = Proxy;
                    if (Skinned._proxySource != null)
                    {
                        Skinned._proxySource.BoundsUpdated += Skinned.ProxyBoundsUpdated;
                        Skinned.ProxyBoundsUpdated();
                    }
                }

                if (skinnedBounds == SkinnedBounds.Explicit)
                {
                    Skinned.MeshRenderer.localBounds = Bounds;
                    Skinned.LocalBoundingBoxAvailable = true;
                    Skinned.SendBoundsUpdated();
                }
            }
            Skinned._currentBoundsMethod = skinnedBounds;
        }
        if (BonesChanged || MeshWasChanged)
        {
            var boneCount = BoneCount;
            var blendShapeCount = BlendShapeCount;
            var weightBonelessOverride = boneCount.GetValueOrDefault() == 0 && blendShapeCount.GetValueOrDefault() > 0;;
            if (weightBonelessOverride) boneCount = 1;
            Skinned.bones = Skinned.bones.EnsureExactSize(boneCount.GetValueOrDefault());
            if (Skinned.bones != null)
            {
                if (weightBonelessOverride)
                {
                    Skinned.bones[0] = Skinned.AttachedGameObject.transform;
                }
                else
                {
                    var num7 = MathX.Min(Skinned.bones.Length, Bones.Count);
                    for (var index = 0; index < num7; ++index)
                    {
                        var obj = Bones[index];
                        if (obj is null) continue;
                        Skinned.bones[index] = obj.ForceGetGameObject().transform;
                    }
                }
            }

            Skinned.MeshRenderer.bones = Skinned.bones;
            Skinned.MeshRenderer.rootBone = weightBonelessOverride
                    ? Skinned.AttachedGameObject.transform
                    : RootBone?.ForceGetGameObject().transform;
        }

        if (BlendShapeWeightsChanged || MeshWasChanged)
        {
            var valueOrDefault = BlendShapeCount.GetValueOrDefault();
            var index1 = 0;
            for (var index2 = MathX.Min(valueOrDefault, BlendShapeWeights.Count); index1 < index2; index1++)
            {
                Skinned.MeshRenderer.SetBlendShapeWeight(index1, BlendShapeWeights[index1]);
            }
            for (; index1 < valueOrDefault; index1++)
            {
                Skinned.MeshRenderer.SetBlendShapeWeight(index1, 0.0f);
            }
        }

        if (Skinned._forceRecalcPerRender) Skinned.MeshRenderer.forceMatrixRecalculationPerRender = true;
        Skinned.SendBoundsUpdated();
    }
}