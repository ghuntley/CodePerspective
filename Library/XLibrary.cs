﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;


namespace XLibrary
{
    public static class XRay
    {
        static TreeForm MainForm;

        internal static XNodeIn RootNode;
        internal static Dictionary<int, XNode> NodeMap = new Dictionary<int, XNode>();

        static int FunctionCount;

        internal static bool ShowOnlyHit;
        internal static bool CoverChange;
        internal static BitArray CoveredFunctions;

        internal const int HitFrames = 20;
        internal static int HitIndex;
        internal static byte[] HitFunctions;

        internal static bool TrackInstances = true;
        internal static byte[] InstanceCount;

        internal static int[] CallingThread;
        internal static byte[] Conflicts;


        public static void TestInit(string path)
        {
            LoadNodeMap(path);

            MainForm = new TreeForm();
            MainForm.Show();
        }

        
        public static void Init()
        {
            string path = Path.Combine(Application.StartupPath , "XRay.dat");

            LoadNodeMap(path);

            FunctionCount++; // so id can be accessed in 0 based index

            HitFunctions = new byte[FunctionCount];
            
            CoveredFunctions = new BitArray(FunctionCount);

            if (TrackInstances)
                InstanceCount = new byte[FunctionCount];

            CallingThread = new int[FunctionCount];
            Conflicts = new byte[FunctionCount];


            Thread gui = new Thread(ShowGui);
            gui.SetApartmentState(ApartmentState.STA);
            gui.Start();
        }

        public static void ShowGui()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm = new TreeForm();
            Application.Run(MainForm);
        }

        static void LoadNodeMap(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open))
            {
                while (stream.Position < stream.Length)
                {
                    XNodeIn node = XNodeIn.Read(stream);
                    NodeMap[node.ID] = node;

                    if (RootNode == null)
                        RootNode = node;

                    if (NodeMap.ContainsKey(node.ParentID))
                    {
                        node.Parent = NodeMap[node.ParentID];
                        node.Parent.Nodes.Add(node);
                    }

                    if (node.ID > FunctionCount)
                        FunctionCount = node.ID; 
                }
            }
        }

        public static void Hit(int thread, int index)
        {
            //if (MainForm == null) 
            //    return; // wait for gui thread to boot up

            if (!CoveredFunctions[index] && NodeMap.ContainsKey(index))
            {
                CoverChange = true;

                XNode node = NodeMap[index];
                while (node != null)
                {
                    CoveredFunctions[node.ID] = true;
                    node = node.Parent;
                }
                // clear cover change on paint
            }

            HitFunctions[index] = HitFrames - 1;


            if(CallingThread[index] != 0 && CallingThread[index] != thread)
                Conflicts[index] = HitFrames - 1;

            CallingThread[index] = thread;

            // keep 6 lists around for diff threads
            // map thread id to list pos
            // keep track of how often each thread list is updated
        }

        public static void Constructed(int index)
        {
            InstanceCount[index]++;
        }

        public static void Deconstructed(int index)
        {
            InstanceCount[index]--;

            // below happens if app calls finalize multiple times (should not happen)
            if (InstanceCount[index] < 0)
            {
                InstanceCount[index] = 0;
                Debug.Assert(false);
            }
        }
    }
}
