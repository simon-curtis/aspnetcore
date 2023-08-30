// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Currently this only deals with inserting streaming content into the DOM.
// Later this will be expanded to include:
//  - Progressive enhancement of navigation and form posting
//  - Preserving existing DOM elements in all the above
//  - The capabilities of Boot.Server.ts and Boot.WebAssembly.ts to handle insertion
//    of interactive components

import { DotNet } from '@microsoft/dotnet-js-interop';
import { setCircuitOptions } from './Boot.Server.Common';
import { setWebAssemblyOptions } from './Boot.WebAssembly.Common';
import { shouldAutoStart } from './BootCommon';
import { Blazor } from './GlobalExports';
import { WebStartOptions } from './Platform/WebStartOptions';
import { attachStreamingRenderingListener } from './Rendering/StreamingRendering';
import { NavigationEnhancementCallbacks, attachProgressivelyEnhancedNavigationListener } from './Services/NavigationEnhancement';
import { WebRootComponentManager } from './Services/WebRootComponentManager';
import { hasProgrammaticEnhancedNavigationHandler, performProgrammaticEnhancedNavigation } from './Services/NavigationUtils';
import { attachComponentDescriptorHandler, registerAllComponentDescriptors } from './Rendering/DomMerging/DomSync';
import { CallbackCollection } from './Services/CallbackCollection';

let started = false;

function boot(options?: Partial<WebStartOptions>) : Promise<void> {
  if (started) {
    throw new Error('Blazor has already started.');
  }

  started = true;

  Blazor._internal.loadWebAssemblyQuicklyTimeout = 3000;

  // Defined here to avoid inadvertently imported enhanced navigation
  // related APIs in WebAssembly or Blazor Server contexts.
  Blazor._internal.hotReloadApplied = () => {
    if (hasProgrammaticEnhancedNavigationHandler()) {
      performProgrammaticEnhancedNavigation(location.href, true);
    }
  };

  setCircuitOptions(options?.circuit);
  setWebAssemblyOptions(options?.webAssembly);

  const rootComponentManager = new WebRootComponentManager(options?.ssr?.circuitInactivityTimeoutMs ?? 2000);
  const enhancedPageUpdateCallbacks = new CallbackCollection();

  Blazor.registerEnhancedPageUpdateCallback = (callback) => enhancedPageUpdateCallbacks.registerCallback(callback);

  const navigationEnhancementCallbacks: NavigationEnhancementCallbacks = {
    documentUpdated: () => {
      rootComponentManager.onDocumentUpdated();
      enhancedPageUpdateCallbacks.enqueueCallbackInvocation();
    },
  };

  attachComponentDescriptorHandler(rootComponentManager);
  attachStreamingRenderingListener(options?.ssr, navigationEnhancementCallbacks);

  if (!options?.ssr?.disableDomPreservation) {
    attachProgressivelyEnhancedNavigationListener(navigationEnhancementCallbacks);
  }

  registerAllComponentDescriptors(document);
  rootComponentManager.onDocumentUpdated();

  return Promise.resolve();
}

Blazor.start = boot;
window['DotNet'] = DotNet;

if (shouldAutoStart()) {
  boot();
}
