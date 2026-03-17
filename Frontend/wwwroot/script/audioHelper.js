window.playAlertSound = (url) => {
    const audio = new Audio(url);
    audio.play().catch(e => console.warn("Audio play failed:", e));
};