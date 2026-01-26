// apiClient.ts
type NavigateParams = { Target: string };
type TabParams = { Tab: number };

class TestClient {
  private baseUrl: string;

  constructor(baseUrl: string = 'http://localhost:5123') {
    this.baseUrl = baseUrl;
  }

  async navigate(params: NavigateParams) {
    return this.post('/navigate', params);
  }

  coop = {
    tab: async (params: TabParams) => this.post('/coop/tab', params),
  };

  private async post(path: string, body: object) {
    const res = await fetch(this.baseUrl + path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });

    if (!res.ok) {
      throw new Error(`Request to ${path} failed: ${res.statusText}`);
    }
    return res.json(); // or res.text() if the response is not JSON
  }
}

// Usage example
const testClient = new TestClient();

(async () => {
  try {
    await testClient.navigate({ Target: 'coopmenu' });
    await testClient.coop.tab({ Tab: 1 });

    console.log('All requests succeeded');
  } catch (err) {
    console.error(err);
  }
})();
