﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MarchingCubes
{

    public interface IMarchingCubeInteractableChunk : IMarchingCubeChunk
    {

        PathTriangle GetTriangleFromRayHit(RaycastHit hit);

        MarchingCubeEntity GetClosestEntity(Vector3 v3);

        void EditPointsAroundRayHit(float delta, RaycastHit hit, int editDistance);

        void EditPointsNextToChunk(IMarchingCubeChunk chunk, Vector3Int entityOrigin, Vector3Int offset, float delta);

        MarchingCubeEntity GetEntityAt(Vector3Int v3);

        MarchingCubeEntity GetEntityAt(int x, int y, int z);

        IMarchingCubeChunkHandler GetChunkHandler { get; }

    }
}