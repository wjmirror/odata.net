//---------------------------------------------------------------------
// <copyright file="ODataWriterCore.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

namespace Microsoft.OData
{
    #region Namespaces
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
#if PORTABLELIB
    using System.Threading.Tasks;
#endif
    using Microsoft.OData.Evaluation;
    using Microsoft.OData.UriParser;
    using Microsoft.OData.Edm;
    using Microsoft.OData.Metadata;
    #endregion Namespaces

    /// <summary>
    /// Base class for OData writers that verifies a proper sequence of write calls on the writer.
    /// </summary>
    internal abstract class ODataWriterCore : ODataWriter, IODataOutputInStreamErrorListener
    {
        /// <summary>The writer validator to use.</summary>
        protected readonly IWriterValidator WriterValidator;

        /// <summary>The output context to write to.</summary>
        private readonly ODataOutputContext outputContext;

        /// <summary>True if the writer was created for writing a resourceSet; false when it was created for writing a resource.</summary>
        private readonly bool writingResourceSet;

        /// <summary>True if the writer was created for writing a delta response; false otherwise.</summary>
        private readonly bool writingDelta;

        /// <summary>If not null, the writer will notify the implementer of the interface of relevant state changes in the writer.</summary>
        private readonly IODataReaderWriterListener listener;

        /// <summary>Stack of writer scopes to keep track of the current context of the writer.</summary>
        private readonly ScopeStack scopes = new ScopeStack();

        /// <summary>
        /// The <see cref="ResourceSetWithoutExpectedTypeValidator"/> to use for entries in this resourceSet.
        /// Only applies when writing a top-level resourceSet; otherwise null.
        /// </summary>
        private readonly ResourceSetWithoutExpectedTypeValidator resourceSetValidator;

        /// <summary>The number of entries which have been started but not yet ended.</summary>
        private int currentResourceDepth;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="outputContext">The output context to write to.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="writingResourceSet">True if the writer is created for writing a resourceSet; false when it is created for writing a resource.</param>
        /// <param name="writingDelta">True if the writer is created for writing a delta response; false otherwise.</param>
        /// <param name="listener">If not null, the writer will notify the implementer of the interface of relevant state changes in the writer.</param>
        protected ODataWriterCore(
            ODataOutputContext outputContext,
            IEdmNavigationSource navigationSource,
            IEdmStructuredType resourceType,
            bool writingResourceSet,
            bool writingDelta = false,
            IODataReaderWriterListener listener = null)
        {
            Debug.Assert(outputContext != null, "outputContext != null");
            Debug.Assert(!writingDelta || outputContext.WritingResponse, "writingResponse must be true when writingDelta is true");

            this.outputContext = outputContext;
            this.writingResourceSet = writingResourceSet;
            this.writingDelta = writingDelta;
            this.WriterValidator = outputContext.WriterValidator;

            // create a collection validator when writing a top-level resourceSet and a user model is present
            if (this.writingResourceSet && this.outputContext.Model.IsUserModel())
            {
                this.resourceSetValidator = new ResourceSetWithoutExpectedTypeValidator();
            }

            if (navigationSource != null && resourceType == null)
            {
                resourceType = this.outputContext.EdmTypeResolver.GetElementType(navigationSource);
            }

            ODataUri odataUri = outputContext.MessageWriterSettings.ODataUri.Clone();

            // Remove key for top level resource
            if (!writingResourceSet && odataUri != null && odataUri.Path != null)
            {
                odataUri.Path = odataUri.Path.TrimEndingKeySegment();
            }

            this.listener = listener;

            this.scopes.Push(new Scope(WriterState.Start, /*item*/null, navigationSource, resourceType, /*skipWriting*/false, outputContext.MessageWriterSettings.SelectedProperties, odataUri));
        }

        /// <summary>
        /// An enumeration representing the current state of the writer.
        /// </summary>
        internal enum WriterState
        {
            /// <summary>The writer is at the start; nothing has been written yet.</summary>
            Start,

            /// <summary>The writer is currently writing a resource.</summary>
            Resource,

            /// <summary>The writer is currently writing a resourceSet.</summary>
            ResourceSet,

            /// <summary>The writer is currently writing a nested resource info (possibly an expanded link but we don't know yet).</summary>
            /// <remarks>
            /// This state is used when a nested resource info was started but we didn't see any children for it yet.
            /// </remarks>
            NestedResourceInfo,

            /// <summary>The writer is currently writing a nested resource info with content.</summary>
            /// <remarks>
            /// This state is used when a nested resource info with either an entity reference link or expanded resourceSet/resource was written.
            /// </remarks>
            NestedResourceInfoWithContent,

            /// <summary>The writer has completed; nothing can be written anymore.</summary>
            Completed,

            /// <summary>The writer is in error state; nothing can be written anymore.</summary>
            Error
        }

        /// <summary>
        /// The current scope for the writer.
        /// </summary>
        protected Scope CurrentScope
        {
            get
            {
                Debug.Assert(this.scopes.Count > 0, "We should have at least one active scope all the time.");
                return this.scopes.Peek();
            }
        }

        /// <summary>
        /// The current state of the writer.
        /// </summary>
        protected WriterState State
        {
            get
            {
                return this.CurrentScope.State;
            }
        }

        /// <summary>
        /// true if the writer should not write any input specified and should just skip it.
        /// </summary>
        protected bool SkipWriting
        {
            get
            {
                return this.CurrentScope.SkipWriting;
            }
        }

        /// <summary>
        /// A flag indicating whether the writer is at the top level.
        /// </summary>
        protected bool IsTopLevel
        {
            get
            {
                Debug.Assert(this.State != WriterState.Start && this.State != WriterState.Completed, "IsTopLevel should only be called while writing the payload.");

                // there is the root scope at the top (when the writer has not started or has completed) 
                // and then the top-level scope (the top-level resource/resourceSet item) as the second scope on the stack
                return this.scopes.Count == 2;
            }
        }

        /// <summary>
        /// Returns the immediate parent link which is being expanded, or null if no such link exists
        /// </summary>
        protected ODataNestedResourceInfo ParentNestedResourceInfo
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource || this.State == WriterState.ResourceSet, "ParentNestedResourceInfo should only be called while writing a resource or a resourceSet.");

                Scope linkScope = this.scopes.ParentOrNull;
                return linkScope == null ? null : (linkScope.Item as ODataNestedResourceInfo);
            }
        }

        /// <summary>
        /// Returns the resource type of the immediate parent resource for which a nested resource info is being written.
        /// </summary>
        protected IEdmStructuredType ParentResourceType
        {
            get
            {
                Debug.Assert(
                    this.State == WriterState.NestedResourceInfo || this.State == WriterState.NestedResourceInfoWithContent,
                    "ParentResourceEntityType should only be called while writing a nested resource info (with or without content).");
                Scope resourceScope = this.scopes.Parent;
                return resourceScope.ResourceType;
            }
        }

        /// <summary>
        /// Returns the navigation source of the immediate parent resource for which a nested resource info is being written.
        /// </summary>
        protected IEdmNavigationSource ParentResourceNavigationSource
        {
            get
            {
                Debug.Assert(
                    this.State == WriterState.NestedResourceInfo || this.State == WriterState.NestedResourceInfoWithContent,
                    "ParentResourceEntityType should only be called while writing a nested resource info (with or without content).");
                Scope resourceScope = this.scopes.Parent;
                return resourceScope.NavigationSource;
            }
        }

        /// <summary>
        /// Returns the number of items seen so far on the current resource set scope.
        /// </summary>
        /// <remarks>Can only be accessed on a resource set scope.</remarks>
        protected int ResourceSetScopeResourceCount
        {
            get
            {
                Debug.Assert(this.State == WriterState.ResourceSet, "ResourceSetScopeResourceCount should only be called while writing a resource set.");
                return ((ResourceSetScope)this.CurrentScope).ResourceCount;
            }
        }

        /// <summary>
        /// Checker to detect duplicate property names.
        /// </summary>
        protected DuplicatePropertyNamesChecker DuplicatePropertyNamesChecker
        {
            get
            {
                Debug.Assert(
                    this.State == WriterState.Resource || this.State == WriterState.NestedResourceInfo || this.State == WriterState.NestedResourceInfoWithContent,
                    "DuplicatePropertyNamesChecker should only be called while writing a resource or an (expanded or deferred) nested resource info.");

                ResourceScope resourceScope;
                switch (this.State)
                {
                    case WriterState.Resource:
                        resourceScope = (ResourceScope)this.CurrentScope;
                        break;
                    case WriterState.NestedResourceInfo:
                    case WriterState.NestedResourceInfoWithContent:
                        resourceScope = (ResourceScope)this.scopes.Parent;
                        break;
                    default:
                        throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_DuplicatePropertyNamesChecker));
                }

                return resourceScope.DuplicatePropertyNamesChecker;
            }
        }

        /// <summary>
        /// The structured type of the current resource.
        /// </summary>
        protected IEdmStructuredType ResourceType
        {
            get
            {
                return this.CurrentScope.ResourceType;
            }
        }

        /// <summary>
        /// Returns the parent nested resource info scope of a resource in an expanded link (if it exists).
        /// The resource can either be the content of the expanded link directly or nested inside a resourceSet.
        /// </summary>
        /// <returns>The parent navigation scope of a resource in an expanded link (if it exists).</returns>
        protected NestedResourceInfoScope ParentNestedResourceInfoScope
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource || this.State == WriterState.ResourceSet, "ParentNestedResourceInfoScope should only be called while writing a resource or a resourceSet.");
                Debug.Assert(this.scopes.Count >= 2, "We should have at least the resource scope and the start scope on the stack.");

                Scope parentScope = this.scopes.Parent;
                if (parentScope.State == WriterState.Start)
                {
                    // Top-level resource.
                    return null;
                }

                if (parentScope.State == WriterState.ResourceSet)
                {
                    Debug.Assert(this.scopes.Count >= 3, "We should have at least the resource scope, the resourceSet scope and the start scope on the stack.");

                    // Get the resourceSet's parent (if any)
                    parentScope = this.scopes.ParentOfParent;
                    if (parentScope.State == WriterState.Start)
                    {
                        // Top-level resourceSet.
                        return null;
                    }
                }

                if (parentScope.State == WriterState.NestedResourceInfoWithContent)
                {
                    // Get the scope of the nested resource info
                    return (NestedResourceInfoScope)parentScope;
                }

                // The parent scope of a resource can only be a resourceSet or an expanded nav link
                throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_ParentNestedResourceInfoScope));
            }
        }

        /// <summary>
        /// Validator to validate consistency of collection items (or null if no such validator applies to the current scope).
        /// </summary>
        private ResourceSetWithoutExpectedTypeValidator CurrentResourceSetValidator
        {
            get
            {
                Debug.Assert(this.State == WriterState.Resource, "CurrentCollectionValidator should only be called while writing a resource.");

                // Only return the collection validator for entries in top-level resource sets
                return this.scopes.Count == 3 ? this.resourceSetValidator : null;
            }
        }

        /// <summary>
        /// Flushes the write buffer to the underlying stream.
        /// </summary>
        public sealed override void Flush()
        {
            this.VerifyCanFlush(true);

            // Make sure we switch to writer state FatalExceptionThrown if an exception is thrown during flushing.
            try
            {
                this.FlushSynchronously();
            }
            catch
            {
                this.EnterScope(WriterState.Error, null);
                throw;
            }
        }

#if PORTABLELIB
        /// <summary>
        /// Asynchronously flushes the write buffer to the underlying stream.
        /// </summary>
        /// <returns>A task instance that represents the asynchronous operation.</returns>
        public sealed override Task FlushAsync()
        {
            this.VerifyCanFlush(false);

            // Make sure we switch to writer state Error if an exception is thrown during flushing.
            return this.FlushAsynchronously().FollowOnFaultWith(t => this.EnterScope(WriterState.Error, null));
        }
#endif

        /// <summary>
        /// Start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">Resource Set/collection to write.</param>
        public sealed override void WriteStart(ODataResourceSet resourceSet)
        {
            this.VerifyCanWriteStartResourceSet(true, resourceSet);
            this.WriteStartResourceSetImplementation(resourceSet);
        }

#if PORTABLELIB
        /// <summary>
        /// Asynchronously start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">Resource Set/collection to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteStartAsync(ODataResourceSet resourceSet)
        {
            this.VerifyCanWriteStartResourceSet(false, resourceSet);
            return TaskUtils.GetTaskForSynchronousOperation(() => this.WriteStartResourceSetImplementation(resourceSet));
        }
#endif

        /// <summary>
        /// Start writing a resource.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        public sealed override void WriteStart(ODataResource resource)
        {
            this.VerifyCanWriteStartResource(true, resource);
            this.WriteStartResourceImplementation(resource);
        }

#if PORTABLELIB
        /// <summary>
        /// Asynchronously start writing a resource.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteStartAsync(ODataResource resource)
        {
            this.VerifyCanWriteStartResource(false, resource);
            return TaskUtils.GetTaskForSynchronousOperation(() => this.WriteStartResourceImplementation(resource));
        }
#endif

        /// <summary>
        /// Start writing a nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">Navigation link to write.</param>
        public sealed override void WriteStart(ODataNestedResourceInfo nestedResourceInfo)
        {
            this.VerifyCanWriteStartNestedResourceInfo(true, nestedResourceInfo);
            this.WriteStartNestedResourceInfoImplementation(nestedResourceInfo);
        }

#if PORTABLELIB
        /// <summary>
        /// Asynchronously start writing a nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">Navigation link to writer.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteStartAsync(ODataNestedResourceInfo nestedResourceInfo)
        {
            this.VerifyCanWriteStartNestedResourceInfo(false, nestedResourceInfo);
            return TaskUtils.GetTaskForSynchronousOperation(() => this.WriteStartNestedResourceInfoImplementation(nestedResourceInfo));
        }
#endif

        /// <summary>
        /// Finish writing a resourceSet/resource/nested resource info.
        /// </summary>
        public sealed override void WriteEnd()
        {
            this.VerifyCanWriteEnd(true);
            this.WriteEndImplementation();
            if (this.CurrentScope.State == WriterState.Completed)
            {
                // Note that we intentionally go through the public API so that if the Flush fails the writer moves to the Error state.
                this.Flush();
            }
        }

#if PORTABLELIB
        /// <summary>
        /// Asynchronously finish writing a resourceSet/resource/nested resource info.
        /// </summary>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        public sealed override Task WriteEndAsync()
        {
            this.VerifyCanWriteEnd(false);
            return TaskUtils.GetTaskForSynchronousOperation(this.WriteEndImplementation)
                .FollowOnSuccessWithTask(
                    task =>
                    {
                        if (this.CurrentScope.State == WriterState.Completed)
                        {
                            // Note that we intentionally go through the public API so that if the Flush fails the writer moves to the Error state.
                            return this.FlushAsync();
                        }
                        else
                        {
                            return TaskUtils.CompletedTask;
                        }
                    });
        }
#endif

        /// <summary>
        /// Writes an entity reference link, which is used to represent binding to an existing resource in a request payload.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <remarks>
        /// This method can only be called for writing request messages. The entity reference link must be surrounded
        /// by a navigation link written through WriteStart/WriteEnd.
        /// The <see cref="ODataNestedResourceInfo.Url"/> will be ignored in that case and the Uri from the <see cref="ODataEntityReferenceLink.Url"/> will be used
        /// as the binding URL to be written.
        /// </remarks>
        public sealed override void WriteEntityReferenceLink(ODataEntityReferenceLink entityReferenceLink)
        {
            this.VerifyCanWriteEntityReferenceLink(entityReferenceLink, true);
            this.WriteEntityReferenceLinkImplementation(entityReferenceLink);
        }

#if PORTABLELIB
        /// <summary>
        /// Asynchronously writes an entity reference link, which is used to represent binding to an existing resource in a request payload.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <returns>A task instance that represents the asynchronous write operation.</returns>
        /// <remarks>
        /// This method can only be called for writing request messages. The entity reference link must be surrounded
        /// by a navigation link written through WriteStart/WriteEnd.
        /// The <see cref="ODataNestedResourceInfo.Url"/> will be ignored in that case and the Uri from the <see cref="ODataEntityReferenceLink.Url"/> will be used
        /// as the binding URL to be written.
        /// </remarks>
        public sealed override Task WriteEntityReferenceLinkAsync(ODataEntityReferenceLink entityReferenceLink)
        {
            this.VerifyCanWriteEntityReferenceLink(entityReferenceLink, false);
            return TaskUtils.GetTaskForSynchronousOperation(() => this.WriteEntityReferenceLinkImplementation(entityReferenceLink));
        }
#endif

        /// <summary>
        /// This method notifies the listener, that an in-stream error is to be written.
        /// </summary>
        /// <remarks>
        /// This listener can choose to fail, if the currently written payload doesn't support in-stream error at this position.
        /// If the listener returns, the writer should not allow any more writing, since the in-stream error is the last thing in the payload.
        /// </remarks>
        void IODataOutputInStreamErrorListener.OnInStreamError()
        {
            this.VerifyNotDisposed();

            // We're in a completed state trying to write an error (we can't write error after the payload was finished as it might
            // introduce another top-level element in XML)
            if (this.State == WriterState.Completed)
            {
                throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromCompleted(this.State.ToString(), WriterState.Error.ToString()));
            }

            this.StartPayloadInStartState();
            this.EnterScope(WriterState.Error, this.CurrentScope.Item);
        }

        /// <summary>
        /// Get instance of the parent resource scope
        /// </summary>
        /// <returns>
        /// The parent resource scope
        /// Or null if there is no parent resource scope
        /// </returns>
        protected ResourceScope GetParentResourceScope()
        {
            ScopeStack scopeStack = new ScopeStack();
            Scope parentResourceScope = null;

            if (this.scopes.Count > 0)
            {
                // pop current scope and push into scope stack
                scopeStack.Push(this.scopes.Pop());
            }

            while (this.scopes.Count > 0)
            {
                Scope scope = this.scopes.Pop();
                scopeStack.Push(scope);

                if (scope is ResourceScope)
                {
                    parentResourceScope = scope;
                    break;
                }
            }

            while (scopeStack.Count > 0)
            {
                Scope scope = scopeStack.Pop();
                this.scopes.Push(scope);
            }

            return parentResourceScope as ResourceScope;
        }


        /// <summary>
        /// Determines whether a given writer state is considered an error state.
        /// </summary>
        /// <param name="state">The writer state to check.</param>
        /// <returns>True if the writer state is an error state; otherwise false.</returns>
        protected static bool IsErrorState(WriterState state)
        {
            return state == WriterState.Error;
        }

        /// <summary>
        /// Gets the projected properties annotation for the specified scope.
        /// </summary>
        /// <param name="currentScope">The scope to get the projected properties annotation for.</param>
        /// <returns>The projected properties annotation for <paramref name="currentScope"/>.</returns>
        protected static ProjectedPropertiesAnnotation GetProjectedPropertiesAnnotation(Scope currentScope)
        {
            ExceptionUtils.CheckArgumentNotNull(currentScope, "currentScope");

            ODataItem currentItem = currentScope.Item;
            return currentItem == null ? null : currentItem.GetAnnotation<ProjectedPropertiesAnnotation>();
        }

        /// <summary>
        /// Check if the object has been disposed; called from all public API methods. Throws an ObjectDisposedException if the object
        /// has already been disposed.
        /// </summary>
        protected abstract void VerifyNotDisposed();

        /// <summary>
        /// Flush the output.
        /// </summary>
        protected abstract void FlushSynchronously();

#if PORTABLELIB
        /// <summary>
        /// Flush the output.
        /// </summary>
        /// <returns>Task representing the pending flush operation.</returns>
        protected abstract Task FlushAsynchronously();
#endif

        /// <summary>
        /// Start writing an OData payload.
        /// </summary>
        protected abstract void StartPayload();

        /// <summary>
        /// Start writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        protected abstract void StartResource(ODataResource resource);

        /// <summary>
        /// Finish writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        protected abstract void EndResource(ODataResource resource);

        /// <summary>
        /// Start writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        protected abstract void StartResourceSet(ODataResourceSet resourceSet);

        /// <summary>
        /// Finish writing an OData payload.
        /// </summary>
        protected abstract void EndPayload();

        /// <summary>
        /// Finish writing a resourceSet.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        protected abstract void EndResourceSet(ODataResourceSet resourceSet);

        /// <summary>
        /// Write a deferred (non-expanded) nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected abstract void WriteDeferredNestedResourceInfo(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Start writing a nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected abstract void StartNestedResourceInfoWithContent(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Finish writing a nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected abstract void EndNestedResourceInfoWithContent(ODataNestedResourceInfo nestedResourceInfo);

        /// <summary>
        /// Write an entity reference link into a navigation link content.
        /// </summary>
        /// <param name="parentNestedResourceInfo">The parent navigation link which is being written around the entity reference link.</param>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        protected abstract void WriteEntityReferenceInNavigationLinkContent(ODataNestedResourceInfo parentNestedResourceInfo, ODataEntityReferenceLink entityReferenceLink);

        /// <summary>
        /// Create a new resource set scope.
        /// </summary>
        /// <param name="resourceSet">The resource set for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly create scope.</returns>
        protected abstract ResourceSetScope CreateResourceSetScope(ODataResourceSet resourceSet, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri);

        /// <summary>
        /// Create a new resource scope.
        /// </summary>
        /// <param name="resource">The resource for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the resources in the resourceSet to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly create scope.</returns>
        protected abstract ResourceScope CreateResourceScope(ODataResource resource, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri);

        /// <summary>
        /// Gets the serialization info for the given resource.
        /// </summary>
        /// <param name="resource">The resource to get the serialization info for.</param>
        /// <returns>The serialization info for the given resource.</returns>
        protected ODataResourceSerializationInfo GetResourceSerializationInfo(ODataResource resource)
        {
            // Need to check for null for the resource since we can be writing a null reference to a navigation property.
            ODataResourceSerializationInfo serializationInfo = resource == null ? null : resource.SerializationInfo;

            // Always try to use the serialization info from the resource first. If it is not found on the resource, use the one inherited from the parent resourceSet.
            // Note that we don't try to guard against inconsistent serialization info between entries and their parent resourceSet.
            if (serializationInfo != null)
            {
                return serializationInfo;
            }

            ResourceSetScope parentResourceSetScope = this.CurrentScope as ResourceSetScope;
            if (parentResourceSetScope != null)
            {
                ODataResourceSet resourceSet = (ODataResourceSet)parentResourceSetScope.Item;
                Debug.Assert(resourceSet != null, "resourceSet != null");

                return resourceSet.SerializationInfo;
            }

            return null;
        }

        /// <summary>
        /// Creates a new nested resource info scope.
        /// </summary>
        /// <param name="writerState">The writer state for the new scope.</param>
        /// <param name="navLink">The nested resource info for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
        /// <param name="entityType">The entity type for the entries in the resourceSet to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly created nested resource info scope.</returns>
        protected virtual NestedResourceInfoScope CreateNestedResourceInfoScope(WriterState writerState, ODataNestedResourceInfo navLink, IEdmNavigationSource navigationSource, IEdmEntityType entityType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
        {
            return new NestedResourceInfoScope(writerState, navLink, navigationSource, entityType, skipWriting, selectedProperties, odataUri);
        }

        /// <summary>
        /// Place where derived writers can perform custom steps before the resource is writen, at the begining of WriteStartEntryImplementation.
        /// </summary>
        /// <param name="resource">Resource to write.</param>
        /// <param name="typeContext">The context object to answer basic questions regarding the type of the resource or resourceSet.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        protected virtual void PrepareResourceForWriteStart(ODataResource resource, ODataResourceTypeContext typeContext, SelectedPropertiesNode selectedProperties)
        {
            // No-op Atom and Verbose JSON. The JSON Light writer will override this method and inject the appropriate metadata builder
            // into the resource before writing.
            // When we support AutoComputePayloadMetadata for all formats in the future, we can inject the metadata builder in here and
            // remove virtual from this method.
        }

        /// <summary>
        /// Validates the media resource on the resource.
        /// </summary>
        /// <param name="resource">The resource to validate.</param>
        /// <param name="entityType">The entity type of the resource.</param>
        protected virtual void ValidateMediaResource(ODataResource resource, IEdmEntityType entityType)
        {
            outputContext.WriterValidator.ValidateMetadataResource(resource, entityType, this.outputContext.Model);
        }

        /// <summary>
        /// Gets the type of the resource and validates it against the model.
        /// </summary>
        /// <param name="resource">The resource to get the type for.</param>
        /// <returns>The validated structured type.</returns>
        protected IEdmStructuredType ValidateResourceType(ODataResource resource)
        {
            if (resource.TypeName == null && this.CurrentScope.ResourceType != null)
            {
                return this.CurrentScope.ResourceType;
            }

            // TODO: Clean up handling of expected types/sets during writing
            return (IEdmStructuredType)TypeNameOracle.ResolveAndValidateTypeName(this.outputContext.Model, resource.TypeName, EdmTypeKind.Complex | EdmTypeKind.Entity, this.WriterValidator);
        }

        /// <summary>
        /// Validates that the ODataResourceSet.DeltaLink is null for the given expanded resourceSet.
        /// </summary>
        /// <param name="resourceSet">The expanded resourceSet in question.</param>
        [SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "An instance field is used in a debug assert.")]
        protected void ValidateNoDeltaLinkForExpandedResourceSet(ODataResourceSet resourceSet)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");
            Debug.Assert(
                this.ParentNestedResourceInfo != null && this.ParentNestedResourceInfo.IsCollection.HasValue && this.ParentNestedResourceInfo.IsCollection.Value == true,
                "This should only be called when writing an expanded resourceSet.");

            if (resourceSet.DeltaLink != null)
            {
                throw new ODataException(Strings.ODataWriterCore_DeltaLinkNotSupportedOnExpandedFeed);
            }
        }

        /// <summary>
        /// Verifies that calling WriteStart resourceSet is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="resourceSet">Resource Set/collection to write.</param>
        private void VerifyCanWriteStartResourceSet(bool synchronousCall, ODataResourceSet resourceSet)
        {
            ExceptionUtils.CheckArgumentNotNull(resourceSet, "resourceSet");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
            this.StartPayloadInStartState();
        }

        /// <summary>
        /// Start writing a resourceSet - implementation of the actual functionality.
        /// </summary>
        /// <param name="resourceSet">The resourceSet to write.</param>
        private void WriteStartResourceSetImplementation(ODataResourceSet resourceSet)
        {
            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.ResourceSet);
            this.EnterScope(WriterState.ResourceSet, resourceSet);

            if (!this.SkipWriting)
            {
                this.InterceptException(() =>
                {
                    // Verify query count
                    if (resourceSet.Count.HasValue)
                    {
                        // Check that Count is not set for requests
                        if (!this.outputContext.WritingResponse)
                        {
                            this.ThrowODataException(Strings.ODataWriterCore_QueryCountInRequest, resourceSet);
                        }

                        // Verify version requirements
                    }

                    this.StartResourceSet(resourceSet);
                });
            }
        }

        /// <summary>
        /// Verifies that calling WriteStart resource is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="resource">Resource/item to write.</param>
        private void VerifyCanWriteStartResource(bool synchronousCall, ODataResource resource)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);

            if (this.State != WriterState.NestedResourceInfo)
            {
                ExceptionUtils.CheckArgumentNotNull(resource, "resource");
            }
        }

        /// <summary>
        /// Start writing a resource - implementation of the actual functionality.
        /// </summary>
        /// <param name="resource">Resource/item to write.</param>
        private void WriteStartResourceImplementation(ODataResource resource)
        {
            this.StartPayloadInStartState();
            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.Resource);
            this.EnterScope(WriterState.Resource, resource);
            if (!this.SkipWriting)
            {
                this.IncreaseResourceDepth();
                this.InterceptException(() =>
                {
                    if (resource != null)
                    {
                        ResourceScope resourceScope = (ResourceScope)this.CurrentScope;
                        IEdmEntityType resourceType = this.ValidateResourceType(resource) as IEdmEntityType;
                        resourceScope.ResourceTypeFromMetadata = resourceScope.ResourceType;

                        NestedResourceInfoScope parentNestedResourceInfoScope = this.ParentNestedResourceInfoScope;
                        if (parentNestedResourceInfoScope != null)
                        {
                            // Validate the consistency of resource types in the expanded resourceSet/resource
                            this.WriterValidator.ValidateResourceInExpandedLink(resourceType, parentNestedResourceInfoScope.ResourceType as IEdmEntityType);
                            resourceScope.ResourceTypeFromMetadata = parentNestedResourceInfoScope.ResourceType;
                        }
                        else if (this.CurrentResourceSetValidator != null)
                        {
                            // Validate the consistency of resource types in the top-level resource sets
                            this.CurrentResourceSetValidator.ValidateResource(resourceType);
                        }

                        resourceScope.ResourceType = resourceType;

                        this.PrepareResourceForWriteStart(resource, resourceScope.GetOrCreateTypeContext(this.outputContext.Model, this.outputContext.WritingResponse), resourceScope.SelectedProperties);

                        this.ValidateMediaResource(resource, resourceType);
                    }

                    this.StartResource(resource);
                });
            }
        }

        /// <summary>
        /// Verifies that calling WriteStart nested resource info is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        /// <param name="nestedResourceInfo">Navigation link to write.</param>
        private void VerifyCanWriteStartNestedResourceInfo(bool synchronousCall, ODataNestedResourceInfo nestedResourceInfo)
        {
            ExceptionUtils.CheckArgumentNotNull(nestedResourceInfo, "nestedResourceInfo");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Start writing a nested resource info - implementation of the actual functionality.
        /// </summary>
        /// <param name="nestedResourceInfo">Navigation link to write.</param>
        private void WriteStartNestedResourceInfoImplementation(ODataNestedResourceInfo nestedResourceInfo)
        {
            this.EnterScope(WriterState.NestedResourceInfo, nestedResourceInfo);

            // If the parent resource has a metadata builder, use that metadatabuilder on the nested resource info as well.
            Debug.Assert(this.scopes.Parent != null, "Navigation link scopes must have a parent scope.");
            Debug.Assert(this.scopes.Parent.Item is ODataResource, "The parent of a nested resource info scope should always be a resource");
            ODataResource parentResource = (ODataResource)this.scopes.Parent.Item;
            if (parentResource.MetadataBuilder != null)
            {
                nestedResourceInfo.MetadataBuilder = parentResource.MetadataBuilder;
            }
        }

        /// <summary>
        /// Verify that calling WriteEnd is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanWriteEnd(bool synchronousCall)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Finish writing a resourceSet/resource/nested resource info.
        /// </summary>
        private void WriteEndImplementation()
        {
            this.InterceptException(() =>
            {
                Scope currentScope = this.CurrentScope;

                switch (currentScope.State)
                {
                    case WriterState.Resource:
                        if (!this.SkipWriting)
                        {
                            ODataResource resource = (ODataResource)currentScope.Item;
                            Debug.Assert(
                                resource != null || this.ParentNestedResourceInfo != null && !this.ParentNestedResourceInfo.IsCollection.Value,
                                "when resource == null, it has to be an expanded single resource navigation");

                            this.EndResource(resource);
                            this.DecreaseResourceDepth();
                        }

                        break;
                    case WriterState.ResourceSet:
                        if (!this.SkipWriting)
                        {
                            ODataResourceSet resourceSet = (ODataResourceSet)currentScope.Item;
                            this.WriterValidator.ValidateResourceSetAtEnd(resourceSet, !this.outputContext.WritingResponse);
                            this.EndResourceSet(resourceSet);
                        }

                        break;
                    case WriterState.NestedResourceInfo:
                        if (!this.outputContext.WritingResponse)
                        {
                            throw new ODataException(Strings.ODataWriterCore_DeferredLinkInRequest);
                        }

                        if (!this.SkipWriting)
                        {
                            ODataNestedResourceInfo link = (ODataNestedResourceInfo)currentScope.Item;
                            this.DuplicatePropertyNamesChecker.CheckForDuplicatePropertyNames(link, false, link.IsCollection);
                            this.WriteDeferredNestedResourceInfo(link);

                            this.MarkNestedResourceInfoAsProcessed(link);
                        }

                        break;
                    case WriterState.NestedResourceInfoWithContent:
                        if (!this.SkipWriting)
                        {
                            ODataNestedResourceInfo link = (ODataNestedResourceInfo)currentScope.Item;
                            this.EndNestedResourceInfoWithContent(link);

                            this.MarkNestedResourceInfoAsProcessed(link);
                        }

                        break;
                    case WriterState.Start:                 // fall through
                    case WriterState.Completed:             // fall through
                    case WriterState.Error:                 // fall through
                        throw new ODataException(Strings.ODataWriterCore_WriteEndCalledInInvalidState(currentScope.State.ToString()));
                    default:
                        throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_WriteEnd_UnreachableCodePath));
                }

                this.LeaveScope();
            });
        }

        /// <summary>
        /// Marks the navigation currently being written as processed in the parent entity's metadata builder.
        /// This is needed so that at the end of writing the resource we can query for all the unwritten navigation properties
        /// defined on the entity type and write out their metadata in fullmetadata mode.
        /// </summary>
        /// <param name="link">The nested resource info being written.</param>
        private void MarkNestedResourceInfoAsProcessed(ODataNestedResourceInfo link)
        {
            Debug.Assert(
                this.CurrentScope.State == WriterState.NestedResourceInfo || this.CurrentScope.State == WriterState.NestedResourceInfoWithContent,
                "This method should only be called when we're writing a nested resource info.");

            ODataResource parent = (ODataResource)this.scopes.Parent.Item;
            Debug.Assert(parent.MetadataBuilder != null, "parent.MetadataBuilder != null");
            parent.MetadataBuilder.MarkNestedResourceInfoProcessed(link.Name);
        }

        /// <summary>
        /// Verifies that calling WriteEntityReferenceLink is valid.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanWriteEntityReferenceLink(ODataEntityReferenceLink entityReferenceLink, bool synchronousCall)
        {
            ExceptionUtils.CheckArgumentNotNull(entityReferenceLink, "entityReferenceLink");

            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Write an entity reference link.
        /// </summary>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        private void WriteEntityReferenceLinkImplementation(ODataEntityReferenceLink entityReferenceLink)
        {
            Debug.Assert(entityReferenceLink != null, "entityReferenceLink != null");

            if (this.outputContext.WritingResponse)
            {
                this.ThrowODataException(Strings.ODataWriterCore_EntityReferenceLinkInResponse, null);
            }

            this.CheckForNestedResourceInfoWithContent(ODataPayloadKind.EntityReferenceLink);
            Debug.Assert(
                this.CurrentScope.Item is ODataNestedResourceInfo,
                "The CheckForNestedResourceInfoWithContent should have verified that entity reference link can only be written inside a nested resource info.");

            if (!this.SkipWriting)
            {
                this.InterceptException(() =>
                {
                    this.WriterValidator.ValidateEntityReferenceLink(entityReferenceLink);
                    this.WriteEntityReferenceInNavigationLinkContent((ODataNestedResourceInfo)this.CurrentScope.Item, entityReferenceLink);
                });
            }
        }

        /// <summary>
        /// Verifies that calling Flush is valid.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCanFlush(bool synchronousCall)
        {
            this.VerifyNotDisposed();
            this.VerifyCallAllowed(synchronousCall);
        }

        /// <summary>
        /// Verifies that a call is allowed to the writer.
        /// </summary>
        /// <param name="synchronousCall">true if the call is to be synchronous; false otherwise.</param>
        private void VerifyCallAllowed(bool synchronousCall)
        {
            if (synchronousCall)
            {
                if (!this.outputContext.Synchronous)
                {
                    throw new ODataException(Strings.ODataWriterCore_SyncCallOnAsyncWriter);
                }
            }
            else
            {
#if PORTABLELIB
                if (this.outputContext.Synchronous)
                {
                    throw new ODataException(Strings.ODataWriterCore_AsyncCallOnSyncWriter);
                }
#else
                Debug.Assert(false, "Async calls are not allowed in this build.");
#endif
            }
        }

        /// <summary>
        /// Enters the 'ExceptionThrown' state and then throws an ODataException with the specified error message.
        /// </summary>
        /// <param name="errorMessage">The error message for the exception.</param>
        /// <param name="item">The OData item to associate with the 'ExceptionThrown' state.</param>
        private void ThrowODataException(string errorMessage, ODataItem item)
        {
            this.EnterScope(WriterState.Error, item);
            throw new ODataException(errorMessage);
        }

        /// <summary>
        /// Checks whether we are currently writing the first top-level element; if so call StartPayload
        /// </summary>
        private void StartPayloadInStartState()
        {
            if (this.State == WriterState.Start)
            {
                this.InterceptException(this.StartPayload);
            }
        }

        /// <summary>
        /// Checks whether we are currently writing a nested resource info and switches to NestedResourceInfoWithContent state if we do.
        /// </summary>
        /// <param name="contentPayloadKind">
        /// What kind of payload kind is being written as the content of a nested resource info.
        /// Only Resource Set, Resource or EntityRefernceLink are allowed.
        /// </param>
        private void CheckForNestedResourceInfoWithContent(ODataPayloadKind contentPayloadKind)
        {
            Debug.Assert(
                contentPayloadKind == ODataPayloadKind.ResourceSet || contentPayloadKind == ODataPayloadKind.Resource || contentPayloadKind == ODataPayloadKind.EntityReferenceLink,
                "Only ResourceSet, Resource or EntityReferenceLink can be specified as a payload kind for a nested resource info content.");

            Scope currentScope = this.CurrentScope;
            if (currentScope.State == WriterState.NestedResourceInfo || currentScope.State == WriterState.NestedResourceInfoWithContent)
            {
                ODataNestedResourceInfo currentNestedResourceInfo = (ODataNestedResourceInfo)currentScope.Item;
                this.InterceptException(() =>
                {
                    if (this.ParentResourceType != null)
                    {
                        IEdmNavigationProperty navigationProperty =
                             this.WriterValidator.ValidateNestedResourceInfo(currentNestedResourceInfo, this.ParentResourceType, contentPayloadKind);
                        if (navigationProperty != null)
                        {
                            this.CurrentScope.ResourceType = navigationProperty.ToEntityType();
                            IEdmNavigationSource parentNavigationSource = this.ParentResourceNavigationSource;

                            this.CurrentScope.NavigationSource = parentNavigationSource == null ? null : parentNavigationSource.FindNavigationTarget(navigationProperty);
                        }
                    }
                });

                if (currentScope.State == WriterState.NestedResourceInfoWithContent)
                {
                    // If we are already in the NestedResourceInfoWithContent state, it means the caller is trying to write two items
                    // into the nested resource info content. This is only allowed for collection navigation property in request.
                    if (this.outputContext.WritingResponse || currentNestedResourceInfo.IsCollection != true)
                    {
                        this.ThrowODataException(Strings.ODataWriterCore_MultipleItemsInNavigationLinkContent, currentNestedResourceInfo);
                    }

                    // Note that we don't invoke duplicate property checker in this case as it's not necessary.
                    // What happens inside the nested resource info was already validated by the condition above.
                    // For collection in request we allow any combination anyway.
                    // For everything else we only allow a single item in the content and thus we will fail above.
                }
                else
                {
                    // we are writing a nested resource info with content; change the state
                    this.PromoteNestedResourceInfoScope();

                    if (!this.SkipWriting)
                    {
                        this.InterceptException(() =>
                        {
                            this.DuplicatePropertyNamesChecker.CheckForDuplicatePropertyNames(
                                    currentNestedResourceInfo,
                                    contentPayloadKind != ODataPayloadKind.EntityReferenceLink,
                                    contentPayloadKind == ODataPayloadKind.ResourceSet);
                            this.StartNestedResourceInfoWithContent(currentNestedResourceInfo);
                        });
                    }
                }
            }
            else
            {
                if (contentPayloadKind == ODataPayloadKind.EntityReferenceLink)
                {
                    this.ThrowODataException(Strings.ODataWriterCore_EntityReferenceLinkWithoutNavigationLink, null);
                }
            }
        }

        /// <summary>
        /// Catch any exception thrown by the action passed in; in the exception case move the writer into
        /// state ExceptionThrown and then rethrow the exception.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        private void InterceptException(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                if (!IsErrorState(this.State))
                {
                    this.EnterScope(WriterState.Error, this.CurrentScope.Item);
                }

                throw;
            }
        }

        /// <summary>
        /// Increments the nested resource count by one and fails if the new value exceeds the maxiumum nested resource depth limit.
        /// </summary>
        private void IncreaseResourceDepth()
        {
            this.currentResourceDepth++;

            if (this.currentResourceDepth > this.outputContext.MessageWriterSettings.MessageQuotas.MaxNestingDepth)
            {
                this.ThrowODataException(Strings.ValidationUtils_MaxDepthOfNestedEntriesExceeded(this.outputContext.MessageWriterSettings.MessageQuotas.MaxNestingDepth), null);
            }
        }

        /// <summary>
        /// Decrements the nested resource count by one.
        /// </summary>
        private void DecreaseResourceDepth()
        {
            Debug.Assert(this.currentResourceDepth > 0, "Resource depth should never become negative.");

            this.currentResourceDepth--;
        }


        /// <summary>
        /// Notifies the implementer of the <see cref="IODataReaderWriterListener"/> interface of relevant state changes in the writer.
        /// </summary>
        /// <param name="newState">The new writer state.</param>
        private void NotifyListener(WriterState newState)
        {
            if (this.listener != null)
            {
                if (IsErrorState(newState))
                {
                    this.listener.OnException();
                }
                else if (newState == WriterState.Completed)
                {
                    this.listener.OnCompleted();
                }
            }
        }

        /// <summary>
        /// Enter a new writer scope; verifies that the transition from the current state into new state is valid
        /// and attaches the item to the new scope.
        /// </summary>
        /// <param name="newState">The writer state to transition into.</param>
        /// <param name="item">The item to associate with the new scope.</param>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "Debug only cast.")]
        private void EnterScope(WriterState newState, ODataItem item)
        {
            this.InterceptException(() => this.ValidateTransition(newState));

            // If the parent scope was marked for skipping content, the new child scope should be as well.
            bool skipWriting = this.SkipWriting;

            Scope currentScope = this.CurrentScope;

            IEdmNavigationSource navigationSource = null;
            IEdmStructuredType resourceType = null;
            SelectedPropertiesNode selectedProperties = currentScope.SelectedProperties;
            ODataUri odataUri = currentScope.ODataUri;

            if (newState == WriterState.Resource || newState == WriterState.ResourceSet)
            {
                navigationSource = currentScope.NavigationSource;
                resourceType = currentScope.ResourceType;
            }

            WriterState currentState = currentScope.State;

            if (this.writingDelta)
            {
                // When writing expanded resource sets in delta response, we start with the parent delta resource.
                // But what we really want in the payload are only the navigation links and expanded resource sets
                // so we need to skip writing the top-level delta resource including its structural properties
                // and instance annotations.
                skipWriting = currentState == WriterState.Start && newState == WriterState.Resource;
            }

            // When writing a nested resource info, check if the link is being projected.
            // If we are projecting properties, but the nav. link is not projected mark it to skip its content.
            if (currentState == WriterState.Resource && newState == WriterState.NestedResourceInfo)
            {
                Debug.Assert(currentScope.Item is ODataResource, "If the current state is Resource the current Item must be resource as well (and not null either).");
                Debug.Assert(item is ODataNestedResourceInfo, "If the new state is NestedResourceInfo the new item must be a nested resource info as well (and not null either).");
                ODataNestedResourceInfo nestedResourceInfo = (ODataNestedResourceInfo)item;

                if (!skipWriting)
                {
                    ProjectedPropertiesAnnotation projectedProperties = GetProjectedPropertiesAnnotation(currentScope);
                    skipWriting = projectedProperties.ShouldSkipProperty(nestedResourceInfo.Name);
                    selectedProperties = currentScope.SelectedProperties.GetSelectedPropertiesForNavigationProperty(currentScope.ResourceType, nestedResourceInfo.Name);

                    if (this.outputContext.WritingResponse)
                    {
                        odataUri = currentScope.ODataUri.Clone();

                        IEdmStructuredType currentResourceType = currentScope.ResourceType;
                        var ComplexProperty = this.WriterValidator.ValidatePropertyDefined(nestedResourceInfo.Name, currentResourceType, false) as IEdmStructuralProperty;
                        if (ComplexProperty == null)
                        {
                            IEdmEntityType currentEntityType = currentScope.ResourceType as IEdmEntityType;
                            IEdmNavigationProperty navigationProperty = this.WriterValidator.ValidateNestedResourceInfo(nestedResourceInfo, currentEntityType, /*payloadKind*/null);
                            if (navigationProperty != null)
                            {
                                resourceType = navigationProperty.ToEntityType();
                                IEdmNavigationSource currentNavigationSource = currentScope.NavigationSource;

                                navigationSource = currentNavigationSource == null ? null : currentNavigationSource.FindNavigationTarget(navigationProperty);

                                SelectExpandClause clause = odataUri.SelectAndExpand;
                                TypeSegment typeCastFromExpand = null;
                                if (clause != null)
                                {
                                    SelectExpandClause subClause;
                                    clause.GetSubSelectExpandClause(nestedResourceInfo.Name, out subClause, out typeCastFromExpand);
                                    odataUri.SelectAndExpand = subClause;
                                }

                                ODataPath odataPath;
                                switch (navigationSource.NavigationSourceKind())
                                {
                                    case EdmNavigationSourceKind.ContainedEntitySet:
                                        if (odataUri.Path == null)
                                        {
                                            throw new ODataException(Strings.ODataWriterCore_PathInODataUriMustBeSetWhenWritingContainedElement);
                                        }

                                        odataPath = odataUri.Path;
                                        if (ShouldAppendKey(currentNavigationSource))
                                        {
                                            ODataItem odataItem = this.CurrentScope.Item;
                                            Debug.Assert(odataItem is ODataResource, "If the current state is Resource the current item must be an ODataResource as well (and not null either).");
                                            ODataResource resource = (ODataResource)odataItem;
                                            KeyValuePair<string, object>[] keys = ODataResourceMetadataContext.GetKeyProperties(resource, this.GetResourceSerializationInfo(resource), currentEntityType);
                                            odataPath = odataPath.AppendKeySegment(keys, currentEntityType, currentNavigationSource);
                                        }

                                        if (odataPath != null && typeCastFromExpand != null)
                                        {
                                            odataPath.Add(typeCastFromExpand);
                                        }

                                        Debug.Assert(navigationSource is IEdmContainedEntitySet, "If the NavigationSourceKind is ContainedEntitySet, the navigationSource must be IEdmContainedEntitySet.");
                                        IEdmContainedEntitySet containedEntitySet = (IEdmContainedEntitySet)navigationSource;
                                        odataPath = odataPath.AppendNavigationPropertySegment(containedEntitySet.NavigationProperty, containedEntitySet);
                                        break;
                                    case EdmNavigationSourceKind.EntitySet:
                                        odataPath = new ODataPath(new EntitySetSegment(navigationSource as IEdmEntitySet));
                                        break;
                                    case EdmNavigationSourceKind.Singleton:
                                        odataPath = new ODataPath(new SingletonSegment(navigationSource as IEdmSingleton));
                                        break;
                                    default:
                                        odataPath = null;
                                        break;
                                }

                                odataUri.Path = odataPath;
                            }
                        }
                    }
                }
            }
            else if (newState == WriterState.Resource && currentState == WriterState.ResourceSet)
            {
                // When we're entering a resource scope on a resourceSet, increment the count of entries on that resourceSet.
                ((ResourceSetScope)currentScope).ResourceCount++;
            }

            this.PushScope(newState, item, navigationSource, resourceType, skipWriting, selectedProperties, odataUri);

            this.NotifyListener(newState);
        }

        /// <summary>
        /// Leave the current writer scope and return to the previous scope. 
        /// When reaching the top-level replace the 'Started' scope with a 'Completed' scope.
        /// </summary>
        /// <remarks>Note that this method is never called once an error has been written or a fatal exception has been thrown.</remarks>
        private void LeaveScope()
        {
            Debug.Assert(this.State != WriterState.Error, "this.State != WriterState.Error");

            this.scopes.Pop();

            // if we are back at the root replace the 'Start' state with the 'Completed' state
            if (this.scopes.Count == 1)
            {
                Scope startScope = this.scopes.Pop();
                Debug.Assert(startScope.State == WriterState.Start, "startScope.State == WriterState.Start");
                this.PushScope(WriterState.Completed, /*item*/null, startScope.NavigationSource, startScope.ResourceType, /*skipWriting*/false, startScope.SelectedProperties, startScope.ODataUri);
                this.InterceptException(this.EndPayload);
                this.NotifyListener(WriterState.Completed);
            }
        }

        /// <summary>
        /// Promotes the current nested resource info scope to a nested resource info scope with content.
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "Second cast only in debug.")]
        private void PromoteNestedResourceInfoScope()
        {
            Debug.Assert(
                this.State == WriterState.NestedResourceInfo,
                "Only a NestedResourceInfo state can be promoted right now. If this changes please review the scope replacement code below.");
            Debug.Assert(
                this.CurrentScope.Item != null && this.CurrentScope.Item is ODataNestedResourceInfo,
                "Item must be a non-null nested resource info.");

            this.ValidateTransition(WriterState.NestedResourceInfoWithContent);
            NestedResourceInfoScope previousScope = (NestedResourceInfoScope)this.scopes.Pop();
            NestedResourceInfoScope newScope = previousScope.Clone(WriterState.NestedResourceInfoWithContent);
            this.scopes.Push(newScope);
        }

        /// <summary>
        /// Verify that the transition from the current state into new state is valid .
        /// </summary>
        /// <param name="newState">The new writer state to transition into.</param>
        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "All the transition checks are encapsulated in this method.")]
        private void ValidateTransition(WriterState newState)
        {
            if (!IsErrorState(this.State) && IsErrorState(newState))
            {
                // we can always transition into an error state if we are not already in an error state
                return;
            }

            switch (this.State)
            {
                case WriterState.Start:
                    if (newState != WriterState.ResourceSet && newState != WriterState.Resource)
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromStart(this.State.ToString(), newState.ToString()));
                    }

                    if (newState == WriterState.ResourceSet && !this.writingResourceSet)
                    {
                        throw new ODataException(Strings.ODataWriterCore_CannotWriteTopLevelFeedWithEntryWriter);
                    }

                    if (newState == WriterState.Resource && this.writingResourceSet)
                    {
                        throw new ODataException(Strings.ODataWriterCore_CannotWriteTopLevelEntryWithFeedWriter);
                    }

                    break;
                case WriterState.Resource:
                    {
                        if (this.CurrentScope.Item == null)
                        {
                            throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromNullEntry(this.State.ToString(), newState.ToString()));
                        }

                        if (newState != WriterState.NestedResourceInfo)
                        {
                            throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromEntry(this.State.ToString(), newState.ToString()));
                        }
                    }

                    break;
                case WriterState.ResourceSet:
                    if (newState != WriterState.Resource)
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromFeed(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.NestedResourceInfo:
                    if (newState != WriterState.NestedResourceInfoWithContent)
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidStateTransition(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.NestedResourceInfoWithContent:
                    if (newState != WriterState.ResourceSet && newState != WriterState.Resource)
                    {
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromExpandedLink(this.State.ToString(), newState.ToString()));
                    }

                    break;
                case WriterState.Completed:
                    // we should never see a state transition when in state 'Completed'
                    throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromCompleted(this.State.ToString(), newState.ToString()));
                case WriterState.Error:
                    if (newState != WriterState.Error)
                    {
                        // No more state transitions once we are in error state except for the fatal error
                        throw new ODataException(Strings.ODataWriterCore_InvalidTransitionFromError(this.State.ToString(), newState.ToString()));
                    }

                    break;
                default:
                    throw new ODataException(Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_ValidateTransition_UnreachableCodePath));
            }
        }

        /// <summary>
        /// Create a new writer scope.
        /// </summary>
        /// <param name="state">The writer state of the scope to create.</param>
        /// <param name="item">The item attached to the scope to create.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The OdataUri info of this scope.</param>
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "Debug.Assert check only.")]
        private void PushScope(WriterState state, ODataItem item, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
        {
            Debug.Assert(
                state == WriterState.Error ||
                state == WriterState.Resource && (item == null || item is ODataResource) ||
                state == WriterState.ResourceSet && item is ODataResourceSet ||
                state == WriterState.NestedResourceInfo && item is ODataNestedResourceInfo ||
                state == WriterState.NestedResourceInfoWithContent && item is ODataNestedResourceInfo ||
                state == WriterState.Start && item == null ||
                state == WriterState.Completed && item == null,
                "Writer state and associated item do not match.");

            Scope scope;
            switch (state)
            {
                case WriterState.Resource:
                    scope = this.CreateResourceScope((ODataResource)item, navigationSource, resourceType, skipWriting, selectedProperties, odataUri);
                    break;
                case WriterState.ResourceSet:
                    scope = this.CreateResourceSetScope((ODataResourceSet)item, navigationSource, resourceType, skipWriting, selectedProperties, odataUri);
                    break;
                case WriterState.NestedResourceInfo:            // fall through
                case WriterState.NestedResourceInfoWithContent:
                    scope = this.CreateNestedResourceInfoScope(state, (ODataNestedResourceInfo)item, navigationSource, resourceType as IEdmEntityType, skipWriting, selectedProperties, odataUri);
                    break;
                case WriterState.Start:                     // fall through
                case WriterState.Completed:                 // fall through
                case WriterState.Error:
                    scope = new Scope(state, item, navigationSource, resourceType, skipWriting, selectedProperties, odataUri);
                    break;
                default:
                    string errorMessage = Strings.General_InternalError(InternalErrorCodes.ODataWriterCore_Scope_Create_UnreachableCodePath);
                    Debug.Assert(false, errorMessage);
                    throw new ODataException(errorMessage);
            }

            this.scopes.Push(scope);
        }

        /// <summary>
        /// Decide whether KeySegment should be appended to ODataPath for certain navigation source.
        /// </summary>
        /// <param name="currentNavigationSource">The navigation source to be evaluated.</param>
        /// <returns>Boolean value indicating whether KeySegment should be appended</returns>
        private static bool ShouldAppendKey(IEdmNavigationSource currentNavigationSource)
        {
            if (currentNavigationSource is IEdmEntitySet)
            {
                return true;
            }

            var currentContainedEntitySet = currentNavigationSource as IEdmContainedEntitySet;
            if (currentContainedEntitySet != null && currentContainedEntitySet.NavigationProperty.Type.TypeKind() == EdmTypeKind.Collection)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Lightweight wrapper for the stack of scopes which exposes a few helper properties for getting parent scopes.
        /// </summary>
        internal sealed class ScopeStack
        {
            /// <summary>
            /// Use a list to store the scopes instead of a true stack so that parent/grandparent lookups will be fast.
            /// </summary>
            private readonly Stack<Scope> scopes = new Stack<Scope>();

            /// <summary>
            /// Initializes a new instance of the <see cref="ScopeStack"/> class.
            /// </summary>
            internal ScopeStack()
            {
            }

            /// <summary>
            /// Gets the count of items in the stack.
            /// </summary>
            internal int Count
            {
                get
                {
                    return this.scopes.Count;
                }
            }

            /// <summary>
            /// Gets the scope below the current scope on top of the stack.
            /// </summary>
            internal Scope Parent
            {
                get
                {
                    Debug.Assert(this.scopes.Count > 1, "this.scopes.Count > 1");
                    Scope current = this.scopes.Pop();
                    Scope parent = this.scopes.Peek();
                    this.scopes.Push(current);
                    return parent;
                }
            }

            /// <summary>
            /// Gets the scope below the parent of the current scope on top of the stack.
            /// </summary>
            internal Scope ParentOfParent
            {
                get
                {
                    Debug.Assert(this.scopes.Count > 2, "this.scopes.Count > 2");
                    Scope current = this.scopes.Pop();
                    Scope parent = this.scopes.Pop();
                    Scope parentOfParent = this.scopes.Peek();
                    this.scopes.Push(parent);
                    this.scopes.Push(current);
                    return parentOfParent;
                }
            }

            /// <summary>
            /// Gets the scope below the current scope on top of the stack or null if there is only one item on the stack or the stack is empty.
            /// </summary>
            internal Scope ParentOrNull
            {
                get
                {
                    return this.Count == 0 ? null : this.Parent;
                }
            }

            /// <summary>
            /// Pushes the specified scope onto the stack.
            /// </summary>
            /// <param name="scope">The scope.</param>
            internal void Push(Scope scope)
            {
                Debug.Assert(scope != null, "scope != null");
                this.scopes.Push(scope);
            }

            /// <summary>
            /// Pops the current scope off the stack.
            /// </summary>
            /// <returns>The popped scope.</returns>
            internal Scope Pop()
            {
                Debug.Assert(this.scopes.Count > 0, "this.scopes.Count > 0");
                return this.scopes.Pop();
            }

            /// <summary>
            /// Peeks at the current scope on the top of the stack.
            /// </summary>
            /// <returns>The current scope at the top of the stack.</returns>
            internal Scope Peek()
            {
                Debug.Assert(this.scopes.Count > 0, "this.scopes.Count > 0");
                return this.scopes.Peek();
            }
        }

        /// <summary>
        /// A writer scope; keeping track of the current writer state and an item associated with this state.
        /// </summary>
        internal class Scope
        {
            /// <summary>The writer state of this scope.</summary>
            private readonly WriterState state;

            /// <summary>The item attached to this scope.</summary>
            private readonly ODataItem item;

            /// <summary>Set to true if the content of the scope should not be written.</summary>
            /// <remarks>This is used when writing navigation links which were not projected on the owning resource.</remarks>
            private readonly bool skipWriting;

            /// <summary>The selected properties for the current scope.</summary>
            private readonly SelectedPropertiesNode selectedProperties;

            /// <summary>The navigation source we are going to write entities for.</summary>
            private IEdmNavigationSource navigationSource;

            /// <summary>The structured type for the resources in the resourceSet to be written (or null if the entity set base type should be used).</summary>
            private IEdmStructuredType resourceType;

            /// <summary>The odata uri info for current scope.</summary>
            private ODataUri odataUri;

            /// <summary>
            /// Constructor creating a new writer scope.
            /// </summary>
            /// <param name="state">The writer state of this scope.</param>
            /// <param name="item">The item attached to this scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of this scope should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal Scope(WriterState state, ODataItem item, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
            {
                this.state = state;
                this.item = item;
                this.resourceType = resourceType;
                this.navigationSource = navigationSource;
                this.skipWriting = skipWriting;
                this.selectedProperties = selectedProperties;
                this.odataUri = odataUri;
            }

            /// <summary>
            /// The structured type for the items in the resource set to be written (or null if the entity set base type should be used).
            /// </summary>
            public IEdmStructuredType ResourceType
            {
                get
                {
                    return this.resourceType;
                }

                set
                {
                    this.resourceType = value;
                }
            }

            /// <summary>
            /// The writer state of this scope.
            /// </summary>
            internal WriterState State
            {
                get
                {
                    return this.state;
                }
            }

            /// <summary>
            /// The item attached to this scope.
            /// </summary>
            internal ODataItem Item
            {
                get
                {
                    return this.item;
                }
            }

            /// <summary>The navigation source we are going to write entities for.</summary>
            internal IEdmNavigationSource NavigationSource
            {
                get
                {
                    return this.navigationSource;
                }

                set
                {
                    this.navigationSource = value;
                }
            }

            /// <summary>The selected properties for the current scope.</summary>
            internal SelectedPropertiesNode SelectedProperties
            {
                get
                {
                    Debug.Assert(this.selectedProperties != null, "this.selectedProperties != null");
                    return this.selectedProperties;
                }
            }

            /// <summary>The odata Uri for the current scope.</summary>
            internal ODataUri ODataUri
            {
                get
                {
                    Debug.Assert(this.odataUri != null, "this.odataUri != null");
                    return this.odataUri;
                }
            }

            /// <summary>
            /// Set to true if the content of this scope should not be written.
            /// </summary>
            internal bool SkipWriting
            {
                get
                {
                    return this.skipWriting;
                }
            }
        }

        /// <summary>
        /// A scope for an resourceSet.
        /// </summary>
        internal abstract class ResourceSetScope : Scope
        {
            /// <summary>The serialization info for the current resourceSet.</summary>
            private readonly ODataResourceSerializationInfo serializationInfo;

            /// <summary>The number of entries in this resourceSet seen so far.</summary>
            private int resourceCount;

            /// <summary>Maintains the write status for each annotation using its key.</summary>
            private InstanceAnnotationWriteTracker instanceAnnotationWriteTracker;

            /// <summary>The type context to answer basic questions regarding the type info of the resource.</summary>
            private ODataResourceTypeContext typeContext;

            /// <summary>
            /// Constructor to create a new resource set scope.
            /// </summary>
            /// <param name="resourceSet">The resourceSet for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal ResourceSetScope(ODataResourceSet resourceSet, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
                : base(WriterState.ResourceSet, resourceSet, navigationSource, resourceType, skipWriting, selectedProperties, odataUri)
            {
                this.serializationInfo = resourceSet.SerializationInfo;
            }

            /// <summary>
            /// The number of entries in this resourceSet seen so far.
            /// </summary>
            internal int ResourceCount
            {
                get
                {
                    return this.resourceCount;
                }

                set
                {
                    this.resourceCount = value;
                }
            }

            /// <summary>
            /// Tracks the write status of the annotations.
            /// </summary>
            internal InstanceAnnotationWriteTracker InstanceAnnotationWriteTracker
            {
                get
                {
                    if (this.instanceAnnotationWriteTracker == null)
                    {
                        this.instanceAnnotationWriteTracker = new InstanceAnnotationWriteTracker();
                    }

                    return this.instanceAnnotationWriteTracker;
                }
            }

            /// <summary>
            /// Gets or creates the type context to answer basic questions regarding the type info of the resource.
            /// </summary>
            /// <param name="model">The Edm model to use.</param>
            /// <param name="writingResponse">True if writing a response payload, false otherwise.</param>
            /// <returns>The type context to answer basic questions regarding the type info of the resource.</returns>
            internal ODataResourceTypeContext GetOrCreateTypeContext(IEdmModel model, bool writingResponse)
            {
                if (this.typeContext == null)
                {
                    this.typeContext = ODataResourceTypeContext.Create(
                        this.serializationInfo,
                        this.NavigationSource,
                        EdmTypeWriterResolver.Instance.GetElementType(this.NavigationSource),
                        this.ResourceType,
                        model,
                        writingResponse);
                }

                return this.typeContext;
            }
        }

        /// <summary>
        /// A scope for a resource.
        /// </summary>
        internal class ResourceScope : Scope
        {
            /// <summary>Checker to detect duplicate property names.</summary>
            private readonly DuplicatePropertyNamesChecker duplicatePropertyNamesChecker;

            /// <summary>The serialization info for the current resource.</summary>
            private readonly ODataResourceSerializationInfo serializationInfo;

            /// <summary>The resource type which was derived from the model (may be either the same as structured type or its base type.</summary>
            private IEdmStructuredType resourceTypeFromMetadata;

            /// <summary>The type context to answer basic questions regarding the type info of the resource.</summary>
            private ODataResourceTypeContext typeContext;

            /// <summary>Maintains the write status for each annotation using its key.</summary>
            private InstanceAnnotationWriteTracker instanceAnnotationWriteTracker;

            /// <summary>
            /// Constructor to create a new resource scope.
            /// </summary>
            /// <param name="resource">The resource for the new scope.</param>
            /// <param name="serializationInfo">The serialization info for the current resource.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="writingResponse">true if we are writing a response, false if it's a request.</param>
            /// <param name="writerSettings">The <see cref="ODataMessageWriterSettings"/> The settings of the writer.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal ResourceScope(ODataResource resource, ODataResourceSerializationInfo serializationInfo, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, bool writingResponse, ODataMessageWriterSettings writerSettings, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
                : base(WriterState.Resource, resource, navigationSource, resourceType, skipWriting, selectedProperties, odataUri)
            {
                Debug.Assert(writerSettings != null, "writerBehavior != null");

                if (resource != null)
                {
                    this.duplicatePropertyNamesChecker = new DuplicatePropertyNamesChecker(
                        writerSettings.AllowDuplicatePropertyNames, 
                        writingResponse, 
                        !writerSettings.EnableFullValidation);
                }

                this.serializationInfo = serializationInfo;
            }

            /// <summary>
            /// The structured type which was derived from the model, i.e. the expected structured type, which may be either the same as structured type or its base type.
            /// For example, if we are writing a resource set of Customers and the current resource is of DerivedCustomer, this.ResourceTypeFromMetadata would be Customer and this.ResourceType would be DerivedCustomer.
            /// </summary>
            public IEdmStructuredType ResourceTypeFromMetadata
            {
                get
                {
                    return this.resourceTypeFromMetadata;
                }

                internal set
                {
                    this.resourceTypeFromMetadata = value;
                }
            }

            /// <summary>
            /// The serialization info for the current resource.
            /// </summary>
            public ODataResourceSerializationInfo SerializationInfo
            {
                get { return this.serializationInfo; }
            }

            /// <summary>
            /// Checker to detect duplicate property names.
            /// </summary>
            internal DuplicatePropertyNamesChecker DuplicatePropertyNamesChecker
            {
                get
                {
                    return this.duplicatePropertyNamesChecker;
                }
            }

            /// <summary>
            /// Tracks the write status of the annotations.
            /// </summary>
            internal InstanceAnnotationWriteTracker InstanceAnnotationWriteTracker
            {
                get
                {
                    if (this.instanceAnnotationWriteTracker == null)
                    {
                        this.instanceAnnotationWriteTracker = new InstanceAnnotationWriteTracker();
                    }

                    return this.instanceAnnotationWriteTracker;
                }
            }

            /// <summary>
            /// Gets or creates the type context to answer basic questions regarding the type info of the resource.
            /// </summary>
            /// <param name="model">The Edm model to use.</param>
            /// <param name="writingResponse">True if writing a response payload, false otherwise.</param>
            /// <returns>The type context to answer basic questions regarding the type info of the resource.</returns>
            public ODataResourceTypeContext GetOrCreateTypeContext(IEdmModel model, bool writingResponse)
            {
                if (this.typeContext == null)
                {
                    this.typeContext = ODataResourceTypeContext.Create(
                        this.serializationInfo,
                        this.NavigationSource,
                        EdmTypeWriterResolver.Instance.GetElementType(this.NavigationSource),
                        this.ResourceTypeFromMetadata,
                        model,
                        writingResponse);
                }

                return this.typeContext;
            }
        }

        /// <summary>
        /// A scope for a nested resource info.
        /// </summary>
        internal class NestedResourceInfoScope : Scope
        {
            /// <summary>
            /// Constructor to create a new nested resource info scope.
            /// </summary>
            /// <param name="writerState">The writer state for the new scope.</param>
            /// <param name="navLink">The nested resource info for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal NestedResourceInfoScope(WriterState writerState, ODataNestedResourceInfo navLink, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
                : base(writerState, navLink, navigationSource, resourceType, skipWriting, selectedProperties, odataUri)
            {
            }

            /// <summary>
            /// Clones this nested resource info scope and sets a new writer state.
            /// </summary>
            /// <param name="newWriterState">The <see cref="WriterState"/> to set.</param>
            /// <returns>The cloned nested resource info scope with the specified writer state.</returns>
            internal virtual NestedResourceInfoScope Clone(WriterState newWriterState)
            {
                return new NestedResourceInfoScope(newWriterState, (ODataNestedResourceInfo)this.Item, this.NavigationSource, this.ResourceType, this.SkipWriting, this.SelectedProperties, this.ODataUri);
            }
        }
    }
}
