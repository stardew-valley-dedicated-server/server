/**
 * Mutual exclusion selection state for test/step/error.
 * Only one can be selected at a time.
 */

import type { Ref } from "vue";
import { ref } from "vue";
import type { ErrorSnapshot, SetupStepSnapshot, TestSnapshot } from "../types/state";

export interface SelectionState {
    selectedTest: Ref<TestSnapshot | null>;
    selectedStep: Ref<SetupStepSnapshot | null>;
    selectedError: Ref<ErrorSnapshot | null>;
    /** Bumped when selected content changes (output, error, screenshots). */
    selectedTestVersion: Ref<number>;
    selectTest: (test: TestSnapshot | null) => void;
    selectStep: (step: SetupStepSnapshot | null) => void;
    selectError: (error: ErrorSnapshot | null) => void;
}

export function useSelectionState(): SelectionState {
    const selectedTest = ref<TestSnapshot | null>(null);
    const selectedStep = ref<SetupStepSnapshot | null>(null);
    const selectedError = ref<ErrorSnapshot | null>(null);
    const selectedTestVersion = ref(0);

    function selectTest(test: TestSnapshot | null) {
        selectedTest.value = test;
        if (test) {
            selectedStep.value = null;
            selectedError.value = null;
        }
        // Bump so OutputPanel's content computeds (output, recordings) re-evaluate
        // against the newly-selected test's already-accumulated data. Without this,
        // a test selected after its output streamed in would render stale.
        selectedTestVersion.value++;
    }

    function selectStep(step: SetupStepSnapshot | null) {
        selectedStep.value = step;
        if (step) {
            selectedTest.value = null;
            selectedError.value = null;
        }
    }

    function selectError(error: ErrorSnapshot | null) {
        selectedError.value = error;
        if (error) {
            selectedTest.value = null;
            selectedStep.value = null;
        }
    }

    return {
        selectedTest,
        selectedStep,
        selectedError,
        selectedTestVersion,
        selectTest,
        selectStep,
        selectError,
    };
}
