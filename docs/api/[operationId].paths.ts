import { usePaths } from 'vitepress-openapi'
import spec from '../assets/openapi.json' with { type: 'json' }

export default {
    paths() {
        return usePaths({ spec })
            .getPathsByVerbs()
            .map(({ operationId, summary }) => ({
                params: {
                    operationId,
                    pageTitle: `${summary} - JunimoServer API`,
                },
            }))
    },
}
