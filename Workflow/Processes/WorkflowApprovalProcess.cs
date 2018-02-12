﻿using log4net;
using System;
using System.Linq;
using System.Reflection;
using Umbraco.Core;
using Umbraco.Core.Persistence;
using Workflow.Helpers;
using Workflow.Models;

namespace Workflow.Processes
{
    public abstract class WorkflowApprovalProcess : IWorkflowProcess
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly PocoRepository Pr = new PocoRepository();

        protected WorkflowType Type { get; set; }
        protected WorkflowInstancePoco Instance;

        private static Database GetDb()
        {
            return ApplicationContext.Current.DatabaseContext.Database;
        }

        # region Public methods
        /// <summary>
        /// Initiates a workflow process instance for this workflow type and persists it to the database.
        /// </summary>
        /// <param name="nodeId">The document that the workflow is for.</param>
        /// <param name="authorUserId">The author submitting the document to workflow</param>
        /// <param name="authorComment">Comments provided by the author.</param>
        /// <returns>The initiated workflow process instance entity.</returns>
        public WorkflowInstancePoco InitiateWorkflow(int nodeId, int authorUserId, string authorComment)
        {
            // use the guid to associate tasks to a workflow instance
            Guid g = Guid.NewGuid();

            // create and persist the new workflow instance
            Instance = new WorkflowInstancePoco(nodeId, authorUserId, authorComment, Type);
            Instance.SetScheduledDate();
            Instance.Guid = g;

            GetDb().Insert(Instance);

            // create the first task in the workflow
            WorkflowTaskInstancePoco taskInstance = CreateApprovalTask(nodeId);

            if (taskInstance.UserGroup == null)
            {
                string errorMessage = $"No approval flow set for document {nodeId} or any of its parent documents. Unable to initiate approval task.";
                Log.Error(errorMessage);
                throw new WorkflowException(errorMessage);
            }

            if (StepApprovalRequired(taskInstance))
            {
                SetPendingTask(taskInstance);
            }
            else
            {
                SetNotRequiredTask(taskInstance, authorUserId, authorComment);
                ActionWorkflow(Instance, WorkflowAction.Approve, authorUserId, taskInstance.Comment);
            }
            return Instance;
        }

        /// <summary>
        /// Processes the action on the workflow instance and persists it to the database.
        /// </summary>
        /// <param name="instance"></param>
        /// <param name="action">The workflow action to be performed</param>
        /// <param name="userId">the user Id of the user who performed the action.</param>
        /// <param name="comment">Any comments the user has provided with the action.</param>
        /// <returns>the actioned workflow process instance entity</returns>
        public WorkflowInstancePoco ActionWorkflow(WorkflowInstancePoco instance, WorkflowAction action, int userId, string comment)
        {
            if (instance != null)
            {
                Instance = instance;

                if (Instance.Status == (int)WorkflowStatus.PendingApproval)
                {
                    // if pending, update to approved or rejected
                    ProcessApprovalAction(action, userId, comment);

                    if (action == WorkflowAction.Approve)
                    {
                        // only progress if there are pending approval tasks, otherwise the flow is complete and the workflow should exit
                        if (Instance.TotalSteps > Instance.TaskInstances.Count)
                        {
                            // create the next task, then check if it should be approved
                            // if it needs approval, 
                            var taskInstance = CreateApprovalTask(Instance.NodeId);

                            if (StepApprovalRequired(taskInstance))
                            {
                                SetPendingTask(taskInstance);
                            }
                            else
                            {
                                SetNotRequiredTask(taskInstance, userId);
                                ActionWorkflow(Instance, WorkflowAction.Approve, userId, $"APPROVAL AT STAGE {Instance.TaskInstances.Count} NOT REQUIRED");
                            }
                        }
                        else
                        {
                            CompleteWorkflow();
                        }
                    }
                    else if (action == WorkflowAction.Reject)
                    {
                        // TODO: reject...
                    }
                }
                else
                {
                    throw new WorkflowException("Workflow instance " + Instance.Id + " is not pending any action.");
                }
                GetDb().Update(Instance);
            }
            else
            {
                if (Instance != null)
                {
                    throw new WorkflowException("Workflow instance " + Instance.Id + " is not found.");
                }
                throw new WorkflowException("Workflow instance is not found.");
            }
            return Instance;
        }

        /// <summary>
        /// Cancels the workflow instance and persists the changes to the database
        /// </summary>
        /// <param name="instance">The workflow instance id for the process to be cancelled</param>
        /// <param name="userId">The user who has cancelled the workflow instance</param>
        /// <param name="reason">The reason given for cancelling the workflow process.</param>
        /// <returns>The cancelled workflow process instance entity</returns>
        public WorkflowInstancePoco CancelWorkflow(WorkflowInstancePoco instance, int userId, string reason)
        {
            if (instance != null)
            {
                Instance = instance;
                Instance.CompletedDate = DateTime.Now;
                Instance.Status = (int)WorkflowStatus.Cancelled;

                var taskInstance = Instance.TaskInstances.FirstOrDefault(ti => ti.TaskStatus == TaskStatus.PendingApproval);
                if (taskInstance != null)
                {
                    // Cancel the task and workflow instances
                    taskInstance.Status = (int)TaskStatus.Cancelled;
                    taskInstance.ActionedByUserId = userId;
                    taskInstance.Comment = reason;
                    taskInstance.CompletedDate = Instance.CompletedDate;

                    GetDb().Update(taskInstance);
                }

                // Send the notification
                GetDb().Update(Instance);
                Notifications.Send(Instance, EmailType.WorkflowCancelled);
            }
            else
            {
                if (Instance != null)
                {
                    throw new WorkflowException("Workflow instance " + Instance.Id + " is not found.");
                }
                throw new WorkflowException("Workflow instance is not found.");
            }

            return Instance;
        }

        protected abstract void CompleteWorkflow();

        #endregion

        #region private methods

        /// <summary>
        /// Update the workflow task status to approve or reject
        /// Sets flag to send email notification if required
        /// Persists all cahanges to the task (stats, completed date, actioned by and comment)
        /// </summary>
        /// <param name="action"></param>
        /// <param name="userId"></param>
        /// <param name="comment"></param>
        private void ProcessApprovalAction(WorkflowAction action, int userId, string comment)
        {
            var taskInstance = Instance.TaskInstances.FirstOrDefault(ti => ti.TaskStatus == TaskStatus.PendingApproval || ti.TaskStatus == TaskStatus.NotRequired);
            if (taskInstance == null) return;

            EmailType? emailType = null;
            var emailRequired = false;

            switch (action)
            {
                case WorkflowAction.Approve:
                    taskInstance.Status = taskInstance.Status == (int)TaskStatus.NotRequired ? (int)TaskStatus.NotRequired : (int)TaskStatus.Approved;
                    break;

                case WorkflowAction.Reject:
                    Instance.Status = (int)WorkflowStatus.Rejected;
                    Instance.CompletedDate = DateTime.Now;
                    taskInstance.Status = (int)TaskStatus.Rejected;
                    emailRequired = true;
                    emailType = EmailType.ApprovalRejection;

                    break;
            }

            taskInstance.CompletedDate = DateTime.Now;
            taskInstance.Comment = comment;
            taskInstance.ActionedByUserId = userId;

            // Send the email after we've done the updates.
            if (emailRequired)
            {
                Notifications.Send(Instance, emailType.Value);
            }

            GetDb().Update(taskInstance);
        }

        /// <summary>
        /// Generate the next approval flow task, returning the new task and a bool indicating whether the publish action should becompleted (ie, this is the end of the flow)
        /// </summary>
        /// <param name="nodeId"></param>
        /// <param name="comment"></param>
        /// <param name="approvalRequired"></param>
        /// <returns></returns>
        private WorkflowTaskInstancePoco CreateApprovalTask(int nodeId)
        {
            var taskInstance =
                new WorkflowTaskInstancePoco(TaskType.Approve)
                {
                    ApprovalStep = Instance.TaskInstances.Count,
                    WorkflowInstanceGuid = Instance.Guid
                };

            Instance.TaskInstances.Add(taskInstance);
            SetApprovalGroup(taskInstance, nodeId);

            GetDb().Insert(taskInstance);

            return taskInstance;
        }

        /// <summary>
        /// Find the next approval group where the current user and change author are not members
        /// </summary>
        /// <param name="taskInstance"></param>
        /// <param name="nodeId"></param>
        private void SetApprovalGroup(WorkflowTaskInstancePoco taskInstance, int nodeId)
        {
            var approvalGroup = Pr.PermissionsForNode(nodeId, 0);
            UserGroupPermissionsPoco group = null;

            if (approvalGroup.Any())
            {
                // approval group length will match the number of groups mapped to the node
                // only interested in the one that corresponds with the index of the most recently added workflow task
                group = approvalGroup.First(g => g.Permission == taskInstance.ApprovalStep);
                SetInstanceTotalSteps(approvalGroup.Count);
            }
            else
            {
                // Recurse up the tree until we find something
                var node = Utility.GetNode(nodeId);
                if (node.Level != 1)
                {
                    SetApprovalGroup(taskInstance, node.Parent.Id);
                }
                else // no group set, check for content-type approval then fallback to default approver
                {
                    var contentTypeApproval = Pr.PermissionsForNode(nodeId, Instance.Node.ContentType.Id).Where(g => g.ContentTypeId != 0).ToList();
                    if (contentTypeApproval.Any())
                    {
                        group = contentTypeApproval.First(g => g.Permission == taskInstance.ApprovalStep);
                        SetInstanceTotalSteps(approvalGroup.Count);
                    }
                    else
                    {
                        group = GetDb().Fetch<UserGroupPermissionsPoco>(SqlHelpers.UserGroupBasic, Pr.GetSettings().DefaultApprover).First();
                        SetInstanceTotalSteps(1);
                    }
                }
            }

            // group will not be null
            if (group != null)
            {
                taskInstance.GroupId = group.GroupId;
                taskInstance.UserGroup = group.UserGroup;
            }
        }

        /// <summary>
        /// Determines whether approval is required by checking if the Author is in the current task group.
        /// TODO: review this. FlowType is probably redundant, and can be better framed as a setting to determine
        /// TODO  if the change author being a member of subsequent groups counts as implicit approval. Setting can 
        /// TODO  be call 'Approve own work' or something similar. Easier to understand than the flow-type options.
        /// </summary>
        /// <returns>true if approval required, false otherwise</returns>
        private bool StepApprovalRequired(WorkflowTaskInstancePoco taskInstance)
        {
            return !taskInstance.UserGroup.IsMember(Instance.AuthorUserId);
        }

        /// <summary>
        /// set the total steps property for a workflow instance
        /// </summary>
        /// <param name="stepCount">The number of approval groups in the current flow (explicit, inherited or content type)</param>
        private void SetInstanceTotalSteps(int stepCount)
        {
            if (Instance.TotalSteps != stepCount)
            {
                Instance.TotalSteps = stepCount;
                GetDb().Update(Instance);
            }
        }

        /// <summary>
        /// Terminates a task, setting it to approved and updating the comment to indicate automatic approval
        /// </summary>
        /// <param name="taskInstance"></param>
        /// <param name="userId"></param>
        private static void SetNotRequiredTask(WorkflowTaskInstancePoco taskInstance, int userId, string comment = "")
        {
            taskInstance.Status = (int)TaskStatus.NotRequired;
            taskInstance.CompletedDate = DateTime.Now;
            taskInstance.Comment = $"{comment}{(string.IsNullOrEmpty(comment) ? "" : " ")}(APPROVAL AT STAGE {taskInstance.ApprovalStep + 1} NOT REQUIRED)";
            taskInstance.ActionedByUserId = userId;

            GetDb().Update(taskInstance);
        }

        /// <summary>
        /// Set the task to pending, notify appropriate groups
        /// </summary>
        /// <param name="taskInstance"></param>
        private void SetPendingTask(WorkflowTaskInstancePoco taskInstance)
        {
            taskInstance.Status = (int)TaskStatus.PendingApproval;
            Instance.Status = (int)WorkflowStatus.PendingApproval;

            Notifications.Send(Instance, EmailType.ApprovalRequest);

            GetDb().Update(taskInstance);
        }

        #endregion
    }
}
