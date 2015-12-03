using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Forms;
using FiddlerAmfParser.AMF3;
using FiddlerAmfParser.Flex.Messaging.Messages;
using FiddlerAmfParser.OutputProcessing;
using FluorineFx;
using FluorineFx.AMF3;
using FluorineFx.Configuration;
using FluorineFx.IO;
using Newtonsoft.Json;

namespace FiddlerAmfParser.DSMessageOutputProcessor
{
    /// <summary>
    /// this is a Plug-in for FiddlerAmfParser
    /// </summary>
    public class DsMessageOutputProcessor : IOutputProcessorPlugin
    {
        /// <summary>
        /// FluorineFx can read any Type in Assembly if we use add mapping in FluorineConfiguration.
        /// When we need AMFDeserializer to read AMFMessage, AMFReader use class name to create a Object.
        /// The Object create by ObjectFactory, ObjectFactory find and load dll file in current path.
        /// </summary>
        static DsMessageOutputProcessor ()
        {
            //register DSMessage Type to FluorineFx
            var classMap = FluorineConfiguration.Instance.FluorineSettings.ClassMappings;
            classMap.Add(typeof(AcknowledgeMessageExt).FullName, "DSK");
            classMap.Add(typeof(AsyncMessageExt).FullName, "DSA");
            classMap.Add(typeof(CommandMessageExt).FullName, "DSC");

            //use this can fix FiddlerScriptOutputProcessor.ProcessAmfBody self referencing loop error 
            Newtonsoft.Json.JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };
        }

        private TreeNode _versionNode = null;
        private TreeNode _headerRootNode = null;
        private TreeNode _bodyRootNode = null;


        #region IOutputProcessorPlugin

        public byte RenderOrder { get { return 200; } }

        public void ProcessAmfMessage(ParserContext ctx, AMFMessage messageData)
        {
            TreeView tree = ctx.Output.TreeView;
            _versionNode = tree.Nodes["Version"];
            _headerRootNode = tree.Nodes.Add("Header", "Header");
            for (var i = 0; i < messageData.HeaderCount; i++)
            {
                var headerNode = _headerRootNode.Nodes.Add("" + i, "[" + i + "]");
                var header = messageData.GetHeaderAt(i);
                headerNode.Nodes.Add("Name", header.Name);
                headerNode.Nodes.Add("MustUnderstand", header.MustUnderstand.ToString());
                headerNode.Nodes.Add(CreateTreeNode("Content", header.Content));
            }

            //save body node for ProcessAmfBody
            _bodyRootNode = tree.Nodes.Add("Body", "Body");

            //for dynamic add node 
            ctx.Output.TreeView.BeforeExpand += TreeView_BeforeExpand;
        }

        public void ProcessAmfBody(ParserContext ctx, AMFBody bodyData)
        {
            //must remove all node after 'body', we will create new tree for body element
            RemoveOtherProcessBody(ctx.Output.TreeView);

            var index = ctx.SourceMessage.Bodies.IndexOf(bodyData);
            var bodyNode = _bodyRootNode.Nodes.Add("" + index, "[" + index + "]");

            bodyNode.Nodes.Add("Call", "Call: " + bodyData.Call);
			bodyNode.Nodes.Add("Target", "Target: " + bodyData.Target);
			bodyNode.Nodes.Add("Method", "Method: " + bodyData.Method);
			bodyNode.Nodes.Add("TypeName", "TypeName: " + bodyData.TypeName);
			bodyNode.Nodes.Add("Response", "Response: " + bodyData.Response);

            //Cant add in once, some Content include object self referencing loop
            //So, when object have property, add a fake child node name 'NONE', 
            //then delay add child node when before expand 
            bodyNode.Nodes.Add(CreateNoChildNode("Content", bodyData.Content));
        }

        #endregion

        #region TreeView

        /// <summary>
        /// when expand fake node, object property must change to TreeNode and add into this node
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TreeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            //Not too deep, some object is self referencing loop, 
            //when use 'ExpandAll' button it will infinite loop and kill process
            var dep = e.Node.FullPath.Split('\\').Length - 1;
            if(dep > 10)
            {
                e.Cancel = true;
                return;
            }

            //fake node's Tag is not null
            if (e.Node.Tag != null)
            {
                e.Node.Nodes.Clear();
                e.Node.Nodes.AddRange(CreateChildNode(e.Node.Tag));
                e.Node.Tag = null;
            }
        }

        /// <summary>
        /// Remove FiddlerAmfParser Default Processor's tree node
        /// </summary>
        /// <param treeName="tree"></param>
        private void RemoveOtherProcessBody(TreeView tree)
        {
            int index = tree.Nodes.IndexOf(_bodyRootNode);
            int count = tree.Nodes.Count - index -1;
            for (int i = 1; i <= count; i++)
            {
                tree.Nodes.RemoveAt(tree.Nodes.Count - 1);
            }
        }

        /// <summary>
        /// Create a TreeNode from an AMF Object
        /// </summary>
        /// <param name="treeName"></param>
        /// <param name="amfObject"></param>
        /// <returns></returns>
        private TreeNode CreateTreeNode(string treeName, object amfObject)
        {
            TreeNode resultTreeNode = new TreeNode(treeName);
            if (amfObject == null)
            {
                string type = ": null";
                resultTreeNode.Text += type;
            }
            else if (amfObject is String)
            {
                string type = ": " + (amfObject as String);
                resultTreeNode.Text += type;
            }
            else if (amfObject is ByteArray)
            {
                ByteArray ba = amfObject as ByteArray;
                string type = ": " + ba.ToGUIDString();
                resultTreeNode.Text += type;
            }
            else if (amfObject is ASObject || amfObject is Dictionary<string, Object>)
            {
                string type = ": {" + amfObject.GetType().Name + "}";
                resultTreeNode.Text += type;

                Dictionary<string, Object> map = amfObject as Dictionary<string, Object>;
                foreach (var kv in map)
                {
                    resultTreeNode.Nodes.Add(CreateTreeNode(kv.Key, kv.Value));
                }
            }
            else if (amfObject is IList)
            {
                IList list = amfObject as IList;
                string type = ": [" + list.Count + "]";
                resultTreeNode.Text += type;
                
                int index = 0;
                foreach (var item in list)
                {
                    resultTreeNode.Nodes.Add(CreateTreeNode("[" + index + "]", item));
                    index++;
                }
            }
            else
            {
                var t = amfObject.GetType();
                
                if (t.IsValueType)
                {
                    string type = ": " + amfObject.ToString();
                    resultTreeNode.Text += type;
                }
                else
                {
                    var ps = t.GetProperties();
                    foreach (var p in ps)
                    {
                        resultTreeNode.Nodes.Add(CreateTreeNode(p.Name, p.GetValue(amfObject, null)));
                    }
                }
            }

            return resultTreeNode;
        }

        /// <summary>
        /// Create a TreeNode for an AMF Object, ignore object property
        /// </summary>
        /// <param name="treeName"></param>
        /// <param name="amfObject"></param>
        /// <returns></returns>
        private TreeNode CreateNoChildNode(string treeName, object amfObject)
        {
            TreeNode resultNode = new TreeNode(treeName);
            if (amfObject == null)
            {
                string type = ": null";
                resultNode.Text += type;
            }
            else if (amfObject is String)
            {
                string type = ": " + (amfObject as String);
                resultNode.Text += type;
            }
            else if (amfObject is ByteArray)
            {
                ByteArray ba = amfObject as ByteArray;
                string type = ": " + ba.ToGUIDString();
                resultNode.Text += type;
            }
            else if (amfObject is ASObject || amfObject is Dictionary<string, Object>)
            {
                string type = ": {" + amfObject.GetType().Name + "}";
                resultNode.Text += type;
                resultNode.Tag = amfObject;
                resultNode.Nodes.Add("NONE", "...");
            }
            else if (amfObject is IList)
            {
                IList list = amfObject as IList;
                string type = ": [" + list.Count + "]";
                resultNode.Text += type;
                resultNode.Tag = amfObject;
                resultNode.Nodes.Add("NONE", "...");
            }
            else
            {
                var t = amfObject.GetType();

                if (t.IsValueType)
                {
                    string type = ": " + amfObject.ToString();
                    resultNode.Text += type;
                }
                else
                {
                    resultNode.Nodes.Add("NONE", "...");
                    resultNode.Tag = amfObject;
                }
            }

            return resultNode;
        }

        /// <summary>
        /// Create an Array TreeNode for an AMF Object property
        /// </summary>
        /// <param name="amfObject"></param>
        /// <returns></returns>
        private TreeNode[] CreateChildNode(object amfObject)
        {
            List<TreeNode> resultNodes = new List<TreeNode>();

            if (amfObject is ASObject || amfObject is Dictionary<string, Object>)
            {
                Dictionary<string, Object> map = amfObject as Dictionary<string, Object>;
                foreach (var kv in map)
                {
                    resultNodes.Add(CreateNoChildNode(kv.Key, kv.Value));
                }
            }
            else if (amfObject is IList)
            {
                IList list = amfObject as IList;
                int index = 0;
                foreach (var item in list)
                {
                    resultNodes.Add(CreateNoChildNode("[" + index + "]", item));
                    index++;
                }
            }
            else
            {
                var t = amfObject.GetType();
                if (!t.IsValueType)
                {
                    var ps = t.GetProperties();
                    foreach (var p in ps)
                    {
                        resultNodes.Add(CreateNoChildNode(p.Name, p.GetValue(amfObject, null)));
                    }
                }
            }

            return resultNodes.ToArray();
        }

        #endregion

    }
}
