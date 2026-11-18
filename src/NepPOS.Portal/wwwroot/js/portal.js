// NepPOS Portal: the little bits that need the browser.
window.portal = {
    // Copies a code or key shown once. Throws if the browser refuses, so the page can say so.
    copy: (text) => navigator.clipboard.writeText(text)
};

// <qr-code data-text="otpauth://..."> draws a QR code (for setting up the authenticator app).
// Drawn here in the browser by qrcode-generator (wwwroot/lib, MIT); the secret never leaves the page.
customElements.define("qr-code", class extends HTMLElement {
    static observedAttributes = ["data-text"];

    connectedCallback() { this.draw(); }
    attributeChangedCallback() { this.draw(); }

    draw() {
        const text = this.dataset.text;
        if (!text || typeof qrcode !== "function") return;
        const qr = qrcode(0, "M");
        qr.addData(text);
        qr.make();
        this.innerHTML = qr.createSvgTag({ cellSize: 4, margin: 0, scalable: true });
        this.setAttribute("role", "img");
        this.setAttribute("aria-label", "QR code for your authenticator app");
    }
});
