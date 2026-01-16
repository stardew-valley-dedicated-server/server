/**
 * Global script to randomly animate feature icons at intervals
 */
export function initRandomIconAnimation() {
    let timeoutId: number | null = null;
    let lastIndex: number | null = null;

    const scheduleNextAnimation = () => {
        // Clear any existing timeout
        if (timeoutId !== null) {
            clearTimeout(timeoutId);
        }

        // Every 1-15s
        const randomInterval = Math.floor(Math.random() * 14000) + 1000;

        console.log(`Next animation in ${randomInterval}ms`);

        timeoutId = window.setTimeout(() => {
            animateRandomIcon();
            scheduleNextAnimation();
        }, randomInterval);
    };

    const playAnimationOnce = (animation: any) => {
        if (!animation) {
            return;
        }

        function onComplete() {
            animation.stop();
            animation.removeEventListener("complete", onComplete);
        }
        animation.addEventListener("complete", onComplete);
        animation.play();
    };

    const animateRandomIcon = () => {
        const icons = document.querySelectorAll("animated-icon");

        if (icons.length === 0) {
            console.log("No icons found");
            return;
        }

        // Get random index different from lastIndex
        let randomIndex: number;
        if (icons.length === 1) {
            randomIndex = 0;
        } else {
            do {
                randomIndex = Math.floor(Math.random() * icons.length);
            } while (randomIndex === lastIndex);
        }

        lastIndex = randomIndex;

        playAnimationOnce((icons[randomIndex] as any).animation);
    };

    // Start the animation cycle
    scheduleNextAnimation();

    // Cleanup function (optional, for when component unmounts)
    return () => {
        if (timeoutId !== null) {
            clearTimeout(timeoutId);
        }
    };
}
