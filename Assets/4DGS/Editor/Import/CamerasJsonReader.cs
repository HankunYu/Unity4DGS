using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // Reads the training cameras.json that 3DGS writes next to its output.
    public static class CamerasJsonReader
    {
        public const string FileName = "cameras.json";

        // Searches the source file's folder and its parents for cameras.json;
        // returns null when there is none.
        public static GaussianSplatAsset.CameraInfo[] FindAndRead(string sourcePath)
        {
            string curPath = sourcePath;
            string camerasPath;
            while (true)
            {
                var dir = Path.GetDirectoryName(curPath);
                if (!Directory.Exists(dir))
                    return null;
                camerasPath = $"{dir}/{FileName}";
                if (File.Exists(camerasPath))
                    break;
                curPath = dir;
            }
            return Read(camerasPath);
        }

        public static GaussianSplatAsset.CameraInfo[] Read(string camerasPath)
        {
            string json = File.ReadAllText(camerasPath);
            var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
            if (jsonCameras == null || jsonCameras.Count == 0)
                return null;

            var result = new GaussianSplatAsset.CameraInfo[jsonCameras.Count];
            for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
            {
                var jsonCam = jsonCameras[camIndex];
                var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
                // the matrix is a "view matrix", not "camera matrix" lol
                var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
                var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
                var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

                axisy *= -1;
                axisz *= -1;

                result[camIndex] = new GaussianSplatAsset.CameraInfo
                {
                    pos = pos,
                    axisX = axisx,
                    axisY = axisy,
                    axisZ = axisz,
                    fov = 25 //@TODO
                };
            }
            return result;
        }

        [Serializable]
        public class JsonCamera
        {
            public int id;
            public string img_name;
            public int width;
            public int height;
            public float[] position;
            public float[][] rotation;
            public float fx;
            public float fy;
        }
    }
}
