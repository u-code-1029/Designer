using System;
using System.Collections.Generic;

namespace DrillFlow.Core.Workflows
{
    public sealed class WorkflowDocument
    {
        public const int CurrentSchemaVersion = 1;

        public WorkflowDocument()
        {
            SchemaVersion = CurrentSchemaVersion;
            Id = Guid.NewGuid();
            Name = "Untitled workflow";
            Description = string.Empty;
            Nodes = new List<WorkflowNode>();
        }

        public int SchemaVersion { get; set; }

        public Guid Id { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        public List<WorkflowNode> Nodes { get; set; }

        public IEnumerable<WorkflowNode> EnumerateNodesDepthFirst()
        {
            if (Nodes == null)
            {
                yield break;
            }

            foreach (var node in Nodes)
            {
                foreach (var descendant in EnumerateNode(node))
                {
                    yield return descendant;
                }
            }
        }

        public WorkflowNode? FindNode(Guid id)
        {
            foreach (var node in EnumerateNodesDepthFirst())
            {
                if (node.Id == id)
                {
                    return node;
                }
            }

            return null;
        }

        public WorkflowNode? FindNode(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            foreach (var node in EnumerateNodesDepthFirst())
            {
                if (string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }

            return null;
        }

        private static IEnumerable<WorkflowNode> EnumerateNode(WorkflowNode? node)
        {
            if (node == null)
            {
                yield break;
            }

            yield return node;
            foreach (var child in node.GetChildren())
            {
                foreach (var descendant in EnumerateNode(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
